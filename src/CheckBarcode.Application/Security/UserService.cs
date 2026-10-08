using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Security;

/// <summary>Action codes defined in actions.json for user administration.</summary>
public static class UserActions
{
    public const string Create = "USER_CREATE";
    public const string Update = "USER_UPDATE";
    public const string ResetPassword = "USER_RESET_PASSWORD";
    public const string Lock = "USER_LOCK";
    public const string Unlock = "USER_UNLOCK";
    public const string Deactivate = "USER_DEACTIVATE";
    public const string Reactivate = "USER_REACTIVATE";
    public const string SetPermissions = "ROLE_PERMISSIONS_CHANGE";
    public const string SavePolicy = "SECURITY_POLICY_CHANGE";
}

/// <summary>User administration (USR-01, 03, 04, 10, 11). Every method goes through the ActionDispatcher.</summary>
public sealed class UserService
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly ISettingsStore _settings;
    private readonly IPasswordHasher _hasher;
    private readonly SessionService _session;
    private readonly PermissionService _permissions;
    private readonly ActionDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly Func<SecurityPolicy> _policy;
    private readonly Action<SecurityPolicy> _setPolicy;

    public UserService(IUserRepository users, IRoleRepository roles, ISettingsStore settings, IPasswordHasher hasher,
        SessionService session, PermissionService permissions, ActionDispatcher dispatcher, IClock clock,
        Func<SecurityPolicy> policy, Action<SecurityPolicy> setPolicy)
    {
        _users = users;
        _roles = roles;
        _settings = settings;
        _hasher = hasher;
        _session = session;
        _permissions = permissions;
        _dispatcher = dispatcher;
        _clock = clock;
        _policy = policy;
        _setPolicy = setPolicy;
    }

    public IReadOnlyList<User> List() => _users.List();
    public IReadOnlyList<User> LockedUsers() => _users.List().Where(u => u.Status == UserStatus.Locked).ToList();
    public IReadOnlyList<Role> Roles() => _roles.List();

    /// <summary>Creates the first Admin on an empty database (password = default, must change).</summary>
    public static void SeedAdmin(IUserRepository users, IPasswordHasher hasher, IClock clock, SecurityPolicy policy, AuditTrail audit)
    {
        if (users.List().Count > 0) return;
        var (hash, salt) = hasher.Hash(policy.DefaultPassword);
        var admin = new User
        {
            Username = "admin",
            FullName = "Administrator",
            RoleCode = Domain.Roles.Admin,
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordChangedUtc = clock.UtcNow,
            MustChangePassword = true,
            CreatedUtc = clock.UtcNow,
            CreatedBy = "SYSTEM",
        };
        admin.Id = users.Insert(admin);
        audit.Write(UserActions.Create, "User", admin.Username, "RoleCode", "", admin.RoleCode, attribution: Attribution.System,
            args: new Dictionary<string, string> { ["detail"] = "seed" });
    }

    public Task<ActionOutcome> CreateAsync(string username, string fullName, string roleCode) =>
        _dispatcher.ExecuteAsync(UserActions.Create, s =>
        {
            username = (username ?? "").Trim();
            if (username.Length < 3 || username.Any(char.IsWhiteSpace)) throw new DomainException("msg.usernameInvalid");
            if (_users.FindByUsername(username) is not null) throw new DomainException("msg.usernameExists", username);
            EnsureRole(roleCode);
            var (hash, salt) = _hasher.Hash(_policy().DefaultPassword);
            var user = new User
            {
                Username = username,
                FullName = (fullName ?? "").Trim(),
                RoleCode = roleCode,
                PasswordHash = hash,
                PasswordSalt = salt,
                PasswordChangedUtc = _clock.UtcNow,
                MustChangePassword = true,
                CreatedUtc = _clock.UtcNow,
                CreatedBy = _session.CurrentUser?.Username ?? "",
            };
            user.Id = _users.Insert(user);
            s.Target("User", user.Username);
            s.Change("FullName", null, user.FullName);
            s.Change("RoleCode", null, user.RoleCode);
            s.Change("Status", null, user.Status);
        });

    public Task<ActionOutcome> UpdateAsync(long userId, string fullName, string roleCode) =>
        _dispatcher.ExecuteAsync(UserActions.Update, s =>
        {
            var user = Get(userId);
            s.Target("User", user.Username);
            EnsureRole(roleCode);
            if (IsSelf(user) && user.RoleCode != roleCode) throw new DomainException("msg.cannotChangeOwnRole");
            if (user.RoleCode == Domain.Roles.Admin && roleCode != Domain.Roles.Admin) EnsureAnotherAdmin(user);
            var before = user.Clone();
            user.FullName = (fullName ?? "").Trim();
            user.RoleCode = roleCode;
            _users.Update(user);
            _session.Refresh(user);
            s.Change("FullName", before.FullName, user.FullName);
            s.Change("RoleCode", before.RoleCode, user.RoleCode);
        });

    public Task<ActionOutcome> ResetPasswordAsync(long userId) =>
        _dispatcher.ExecuteAsync(UserActions.ResetPassword, s =>
        {
            var user = Get(userId);
            s.Target("User", user.Username);
            SetDefaultPassword(user);
            _users.Update(user);
            s.Change("MustChangePassword", false, true);
        });

    public Task<ActionOutcome> LockAsync(long userId) =>
        _dispatcher.ExecuteAsync(UserActions.Lock, s =>
        {
            var user = Get(userId);
            s.Target("User", user.Username);
            if (IsSelf(user)) throw new DomainException("msg.cannotLockSelf");
            if (user.RoleCode == Domain.Roles.Admin) EnsureAnotherAdmin(user);
            var old = user.Status;
            user.Status = UserStatus.Locked;
            user.LockReason = LockReason.LockedByAdmin;
            user.LockedUtc = _clock.UtcNow;
            _users.Update(user);
            s.Change("Status", old, user.Status);
            s.Args["lockReason"] = nameof(LockReason.LockedByAdmin);
        });

    /// <summary>Unlock re-issues the default password; the user must change it at next login (feature list).</summary>
    public Task<ActionOutcome> UnlockAsync(long userId) =>
        _dispatcher.ExecuteAsync(UserActions.Unlock, s =>
        {
            var user = Get(userId);
            s.Target("User", user.Username);
            if (user.Status != UserStatus.Locked) throw new DomainException("msg.userNotLocked");
            if (IsSelf(user)) throw new DomainException("msg.cannotUnlockSelf");
            var oldReason = user.LockReason;
            user.Status = UserStatus.Active;
            user.LockReason = LockReason.None;
            user.LockedUtc = null;
            SetDefaultPassword(user);
            _users.Update(user);
            s.Change("Status", UserStatus.Locked, UserStatus.Active);
            s.Args["lockReason"] = oldReason.ToString();
        });

    public Task<ActionOutcome> DeactivateAsync(long userId) =>
        _dispatcher.ExecuteAsync(UserActions.Deactivate, s =>
        {
            var user = Get(userId);
            s.Target("User", user.Username);
            if (IsSelf(user)) throw new DomainException("msg.cannotDeactivateSelf");
            if (user.RoleCode == Domain.Roles.Admin) EnsureAnotherAdmin(user);
            var old = user.Status;
            user.Status = UserStatus.Inactive;
            _users.Update(user);
            s.Change("Status", old, user.Status);
        });

    public Task<ActionOutcome> ReactivateAsync(long userId) =>
        _dispatcher.ExecuteAsync(UserActions.Reactivate, s =>
        {
            var user = Get(userId);
            s.Target("User", user.Username);
            if (user.Status != UserStatus.Inactive) throw new DomainException("msg.userNotInactive");
            user.Status = UserStatus.Active;
            SetDefaultPassword(user);
            _users.Update(user);
            s.Change("Status", UserStatus.Inactive, UserStatus.Active);
        });

    public Task<ActionOutcome> SetRolePermissionsAsync(string roleCode, IEnumerable<string> permissions) =>
        _dispatcher.ExecuteAsync(UserActions.SetPermissions, s =>
        {
            EnsureRole(roleCode);
            var next = new HashSet<string>(permissions.Where(p => Permissions.All.Contains(p)), StringComparer.OrdinalIgnoreCase);
            if (roleCode == Domain.Roles.Admin && (!next.Contains(Permissions.UserManage) || !next.Contains(Permissions.SecuritySettings)))
                throw new DomainException("msg.adminMustKeepUserManage");
            var current = new HashSet<string>(_roles.PermissionsOf(roleCode), StringComparer.OrdinalIgnoreCase);
            s.Target("Role", roleCode);
            foreach (var p in next.Except(current)) s.Change(p, false, true);
            foreach (var p in current.Except(next)) s.Change(p, true, false);
            _roles.SetPermissions(roleCode, next);
            _permissions.Reload();
        });

    public Task<ActionOutcome> SavePolicyAsync(SecurityPolicy policy) =>
        _dispatcher.ExecuteAsync(UserActions.SavePolicy, s =>
        {
            if (policy.MaxFailedLogins < 1 || policy.PasswordMinLength < 4 || policy.IdleLogoutMinutes <= 0)
                throw new DomainException("msg.invalidPolicy");
            s.Target("Setting", "Security");
            var old = _policy().ToSettings().ToDictionary(x => x.Key, x => x.Value);
            foreach (var (key, value) in policy.ToSettings())
            {
                var before = old.TryGetValue(key, out var v) ? v : "";
                if (key == SecurityPolicy.KeyDefaultPassword)
                    s.Change(key, before == value ? "***" : "***(old)", "***");
                else
                    s.Change(key, before, value);
                _settings.Set(key, value);
            }
            _setPolicy(policy);
        });

    private void SetDefaultPassword(User user)
    {
        var (hash, salt) = _hasher.Hash(_policy().DefaultPassword);
        _users.AddPasswordHistory(user.Id, user.PasswordHash, user.PasswordSalt, _clock.UtcNow);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordChangedUtc = _clock.UtcNow;
        user.MustChangePassword = true;
        user.FailedCount = 0;
    }

    private User Get(long id) => _users.FindById(id) ?? throw new DomainException("msg.userNotFound", id);

    private bool IsSelf(User user) => _session.CurrentUser?.Id == user.Id;

    private void EnsureRole(string roleCode)
    {
        if (!_roles.List().Any(r => r.Code == roleCode)) throw new DomainException("msg.roleNotFound", roleCode);
    }

    private void EnsureAnotherAdmin(User user)
    {
        var others = _users.List().Count(u => u.Id != user.Id && u.RoleCode == Domain.Roles.Admin && u.Status == UserStatus.Active);
        if (others == 0) throw new DomainException("msg.lastAdmin");
    }
}
