using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Security;

public enum LoginStatus
{
    Success,
    InvalidCredentials,
    Locked,
    Inactive,
    /// <summary>Password correct but must be changed first (new user, reset, expired).</summary>
    MustChangePassword,
}

public sealed record LoginResult(LoginStatus Status, int RemainingAttempts = 0, User? User = null, bool Expired = false);

public sealed record CheckResult(bool Ok, string MessageKey = "", object[]? Args = null)
{
    public static readonly CheckResult Success = new(true);
    public static CheckResult Fail(string key, params object[] args) => new(false, key, args);
}

/// <summary>Login, logout, password change and e-signature verification (USR-02..07, 11.200).</summary>
public sealed class AuthService
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly SessionService _session;
    private readonly AuditTrail _audit;
    private readonly IClock _clock;
    private readonly Func<SecurityPolicy> _policy;

    public AuthService(IUserRepository users, IPasswordHasher hasher, SessionService session, AuditTrail audit, IClock clock, Func<SecurityPolicy> policy)
    {
        _users = users;
        _hasher = hasher;
        _session = session;
        _audit = audit;
        _clock = clock;
        _policy = policy;
    }

    private static Attribution As(User u) => new(u.Username, u.RoleCode, SessionState.Active);

    public LoginResult Login(string username, string password)
    {
        username = (username ?? "").Trim();
        var user = _users.FindByUsername(username);
        if (user is null)
        {
            _audit.Write(AuditCodes.LoginFailed, "User", username, args: new Dictionary<string, string> { ["detail"] = "unknown" },
                attribution: new Attribution(username, "", SessionState.None));
            return new LoginResult(LoginStatus.InvalidCredentials);
        }
        if (user.Status == UserStatus.Inactive)
        {
            _audit.Write(AuditCodes.LoginFailed, "User", user.Username, args: new Dictionary<string, string> { ["detail"] = "inactive" },
                attribution: new Attribution(user.Username, user.RoleCode, SessionState.None));
            return new LoginResult(LoginStatus.Inactive);
        }
        if (user.Status == UserStatus.Locked)
        {
            _audit.Write(AuditCodes.LoginRejectedLocked, "User", user.Username, attribution: new Attribution(user.Username, user.RoleCode, SessionState.None));
            return new LoginResult(LoginStatus.Locked, 0, user);
        }

        if (!_hasher.Verify(password ?? "", user.PasswordHash, user.PasswordSalt))
        {
            var remaining = RegisterFailure(user, "login");
            return remaining <= 0 ? new LoginResult(LoginStatus.Locked, 0, user) : new LoginResult(LoginStatus.InvalidCredentials, remaining, user);
        }

        if (user.FailedCount > 0)
        {
            user.FailedCount = 0;
            _users.Update(user);
        }

        var expired = IsPasswordExpired(user);
        if (user.MustChangePassword || expired)
        {
            if (expired)
                _audit.Write(AuditCodes.PasswordExpired, "User", user.Username, attribution: new Attribution(user.Username, user.RoleCode, SessionState.None));
            return new LoginResult(LoginStatus.MustChangePassword, 0, user, expired);
        }

        if (_session.CurrentUser is { } previous && previous.Id != user.Id) Logout(LogoutReason.Manual);
        _session.Begin(user);
        _audit.Write(AuditCodes.LoginOk, "User", user.Username, attribution: As(user));
        return new LoginResult(LoginStatus.Success, 0, user);
    }

    public void Logout(LogoutReason reason)
    {
        var user = _session.CurrentUser;
        if (user is null) return;
        _audit.Write(reason == LogoutReason.Idle ? AuditCodes.LogoutIdle : AuditCodes.Logout, "User", user.Username,
            args: new Dictionary<string, string> { ["reason"] = reason.ToString() }, attribution: As(user));
        _session.End(reason);
    }

    /// <summary>Called periodically by the UI timer; logs out after the idle limit.</summary>
    public bool CheckIdle()
    {
        var user = _session.CurrentUser;
        if (user is null) return false;
        var limit = TimeSpan.FromMinutes(_policy().IdleLogoutMinutes);
        if (limit <= TimeSpan.Zero || _session.IdleFor < limit) return false;
        Logout(LogoutReason.Idle);
        return true;
    }

    public bool IsPasswordExpired(User user)
    {
        if (user.RoleCode == Roles.Admin) return false;
        var months = _policy().PasswordExpiryMonths;
        if (months <= 0) return false;
        return _clock.UtcNow >= user.PasswordChangedUtc.AddMonths(months);
    }

    /// <summary>Changes the user's own password. Ends the session so the user must log in again (feature list).</summary>
    public CheckResult ChangePassword(string username, string currentPassword, string newPassword, string confirmPassword)
    {
        var user = _users.FindByUsername(username);
        if (user is null || user.Status != UserStatus.Active) return CheckResult.Fail("msg.invalidCredentials");
        if (!_hasher.Verify(currentPassword ?? "", user.PasswordHash, user.PasswordSalt))
        {
            var remaining = RegisterFailure(user, "changePassword");
            return remaining <= 0 ? CheckResult.Fail("msg.accountLocked") : CheckResult.Fail("msg.wrongPasswordRemaining", remaining);
        }
        var policyCheck = ValidateNewPassword(user, newPassword, confirmPassword);
        if (!policyCheck.Ok) return policyCheck;

        var (hash, salt) = _hasher.Hash(newPassword);
        _users.AddPasswordHistory(user.Id, user.PasswordHash, user.PasswordSalt, _clock.UtcNow);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordChangedUtc = _clock.UtcNow;
        user.MustChangePassword = false;
        user.FailedCount = 0;
        _users.Update(user);
        _audit.Write(AuditCodes.PasswordChanged, "User", user.Username, attribution: new Attribution(user.Username, user.RoleCode,
            _session.CurrentUser?.Id == user.Id ? SessionState.Active : SessionState.None));
        if (_session.CurrentUser?.Id == user.Id) Logout(LogoutReason.PasswordChanged);
        return CheckResult.Success;
    }

    public CheckResult ValidateNewPassword(User user, string newPassword, string confirmPassword)
    {
        var policy = _policy();
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < policy.PasswordMinLength)
            return CheckResult.Fail("msg.passwordTooShort", policy.PasswordMinLength);
        if (newPassword != confirmPassword) return CheckResult.Fail("msg.passwordMismatch");
        if (newPassword == policy.DefaultPassword) return CheckResult.Fail("msg.passwordIsDefault");
        if (_hasher.Verify(newPassword, user.PasswordHash, user.PasswordSalt)) return CheckResult.Fail("msg.passwordReused");
        foreach (var (hash, salt) in _users.RecentPasswords(user.Id, Math.Max(0, policy.PasswordHistoryCount - 1)))
            if (_hasher.Verify(newPassword, hash, salt)) return CheckResult.Fail("msg.passwordReused");
        return CheckResult.Success;
    }

    /// <summary>
    /// E-signature check: signer must be the logged-in user and the password must match.
    /// Failures count towards the lockout like a failed login.
    /// </summary>
    public CheckResult VerifySignature(string username, string password)
    {
        var current = _session.CurrentUser;
        if (current is null) return CheckResult.Fail("msg.loginRequired");
        if (!string.Equals(current.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase))
            return CheckResult.Fail("msg.signerMustBeCurrentUser");
        var user = _users.FindById(current.Id);
        if (user is null || user.Status != UserStatus.Active) return CheckResult.Fail("msg.accountLocked");
        if (!_hasher.Verify(password ?? "", user.PasswordHash, user.PasswordSalt))
        {
            var remaining = RegisterFailure(user, "signature");
            if (remaining <= 0)
            {
                Logout(LogoutReason.Manual);
                return CheckResult.Fail("msg.accountLocked");
            }
            return CheckResult.Fail("msg.wrongPasswordRemaining", remaining);
        }
        if (user.FailedCount > 0)
        {
            user.FailedCount = 0;
            _users.Update(user);
        }
        return CheckResult.Success;
    }

    /// <summary>Increments the failure counter, locks at the threshold. Returns remaining attempts.</summary>
    private int RegisterFailure(User user, string context)
    {
        var max = Math.Max(1, _policy().MaxFailedLogins);
        user.FailedCount++;
        var remaining = max - user.FailedCount;
        var who = new Attribution(user.Username, user.RoleCode, SessionState.None);
        _audit.Write(AuditCodes.LoginFailed, "User", user.Username,
            args: new Dictionary<string, string> { ["count"] = user.FailedCount.ToString(), ["max"] = max.ToString(), ["context"] = context },
            attribution: who);
        if (remaining <= 0)
        {
            user.Status = UserStatus.Locked;
            user.LockReason = LockReason.WrongPassword;
            user.LockedUtc = _clock.UtcNow;
            _audit.Write(AuditCodes.AccountLocked, "User", user.Username, "Status", nameof(UserStatus.Active), nameof(UserStatus.Locked),
                args: new Dictionary<string, string> { ["lockReason"] = nameof(LockReason.WrongPassword), ["count"] = user.FailedCount.ToString() },
                attribution: who);
        }
        _users.Update(user);
        _session.Refresh(user);
        return Math.Max(0, remaining);
    }
}
