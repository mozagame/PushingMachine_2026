using System.Globalization;
using System.Security.Cryptography;
using CheckBarcode.Application.Ports;

namespace CheckBarcode.Application.Security;

/// <summary>Security settings editable by Admin (stored in Setting table, audited).</summary>
public sealed class SecurityPolicy
{
    public const string KeyMaxFailed = "Security.MaxFailedLogins";
    public const string KeyIdleMinutes = "Security.IdleLogoutMinutes";
    public const string KeyExpiryMonths = "Security.PasswordExpiryMonths";
    public const string KeyMinLength = "Security.PasswordMinLength";
    public const string KeyHistory = "Security.PasswordHistoryCount";
    public const string KeyDefaultPassword = "Security.DefaultPassword";

    /// <summary>Wrong passwords before lock (BTĐ: 5).</summary>
    public int MaxFailedLogins { get; set; } = 5;
    /// <summary>Idle time before automatic logout (feature list: 3 min).</summary>
    public double IdleLogoutMinutes { get; set; } = 3;
    /// <summary>Password validity in calendar months (feature list: 2).</summary>
    public int PasswordExpiryMonths { get; set; } = 2;
    public int PasswordMinLength { get; set; } = 6;
    /// <summary>How many previous passwords may not be reused (1 = must differ from current).</summary>
    public int PasswordHistoryCount { get; set; } = 1;
    /// <summary>Password given to new / unlocked users; must be changed at first login.</summary>
    public string DefaultPassword { get; set; } = "Abc@1234";

    public static SecurityPolicy Load(ISettingsStore store)
    {
        var p = new SecurityPolicy();
        int I(string k, int d) => int.TryParse(store.Get(k), out var v) ? v : d;
        double D(string k, double d) => double.TryParse(store.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        p.MaxFailedLogins = I(KeyMaxFailed, p.MaxFailedLogins);
        p.IdleLogoutMinutes = D(KeyIdleMinutes, p.IdleLogoutMinutes);
        p.PasswordExpiryMonths = I(KeyExpiryMonths, p.PasswordExpiryMonths);
        p.PasswordMinLength = I(KeyMinLength, p.PasswordMinLength);
        p.PasswordHistoryCount = I(KeyHistory, p.PasswordHistoryCount);
        p.DefaultPassword = store.Get(KeyDefaultPassword) ?? p.DefaultPassword;
        return p;
    }

    public IEnumerable<(string Key, string Value)> ToSettings()
    {
        yield return (KeyMaxFailed, MaxFailedLogins.ToString(CultureInfo.InvariantCulture));
        yield return (KeyIdleMinutes, IdleLogoutMinutes.ToString(CultureInfo.InvariantCulture));
        yield return (KeyExpiryMonths, PasswordExpiryMonths.ToString(CultureInfo.InvariantCulture));
        yield return (KeyMinLength, PasswordMinLength.ToString(CultureInfo.InvariantCulture));
        yield return (KeyHistory, PasswordHistoryCount.ToString(CultureInfo.InvariantCulture));
        yield return (KeyDefaultPassword, DefaultPassword);
    }
}

public interface IPasswordHasher
{
    (string Hash, string Salt) Hash(string password);
    bool Verify(string password, string hash, string salt);
}

/// <summary>PBKDF2-SHA256, 100 000 iterations, 16-byte salt, 32-byte key (USR-12).</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private readonly int _iterations;
    public Pbkdf2PasswordHasher(int iterations = 100_000) => _iterations = iterations;

    public (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, 32);
        return ($"{_iterations}:{Convert.ToBase64String(key)}", Convert.ToBase64String(salt));
    }

    public bool Verify(string password, string hash, string salt)
    {
        var parts = hash.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var iterations)) return false;
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
