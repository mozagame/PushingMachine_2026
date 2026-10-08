using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Security;

public enum LogoutReason
{
    Manual,
    Idle,
    PasswordChanged,
    Shutdown,
}

/// <summary>Who is attributed to an event at a given moment.</summary>
public readonly record struct Attribution(string Username, string Role, SessionState State)
{
    public static readonly Attribution System = new("SYSTEM", "", SessionState.None);
}

/// <summary>
/// Holds the logged-in user, tracks idle time (USR-07) and keeps the last user so that machine
/// events during an expired session are attributed to them with <see cref="SessionState.Expired"/>.
/// </summary>
public sealed class SessionService
{
    private readonly IClock _clock;
    private readonly Func<SecurityPolicy> _policy;
    private readonly object _gate = new();
    private DateTime _lastActivityUtc;

    public SessionService(IClock clock, Func<SecurityPolicy> policy)
    {
        _clock = clock;
        _policy = policy;
        _lastActivityUtc = clock.UtcNow;
    }

    public User? CurrentUser { get; private set; }
    public User? LastUser { get; private set; }
    public bool IsLoggedIn => CurrentUser is not null;

    /// <summary>Raised after login / logout (argument: reason or null for login).</summary>
    public event EventHandler<LogoutReason?>? Changed;

    public Attribution Attribution
    {
        get
        {
            lock (_gate)
            {
                if (CurrentUser is { } u) return new Attribution(u.Username, u.RoleCode, SessionState.Active);
                if (LastUser is { } l) return new Attribution(l.Username, l.RoleCode, SessionState.Expired);
                return Attribution.System;
            }
        }
    }

    public void Begin(User user)
    {
        lock (_gate)
        {
            CurrentUser = user.Clone();
            LastUser = CurrentUser;
            _lastActivityUtc = _clock.UtcNow;
        }
        Changed?.Invoke(this, null);
    }

    public void End(LogoutReason reason)
    {
        lock (_gate)
        {
            if (CurrentUser is null) return;
            CurrentUser = null;
        }
        Changed?.Invoke(this, reason);
    }

    /// <summary>Call on every mouse / touch / key input.</summary>
    public void Touch()
    {
        lock (_gate) _lastActivityUtc = _clock.UtcNow;
    }

    public TimeSpan IdleFor
    {
        get { lock (_gate) return _clock.UtcNow - _lastActivityUtc; }
    }

    /// <summary>Refreshes the cached user after an admin change (role, status).</summary>
    public void Refresh(User user)
    {
        lock (_gate)
        {
            if (CurrentUser?.Id == user.Id) CurrentUser = user.Clone();
            if (LastUser?.Id == user.Id) LastUser = user.Clone();
        }
    }
}
