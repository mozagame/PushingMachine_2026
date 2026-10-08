using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Security;

/// <summary>Answers "may the current user do X" from the role → permission matrix (USR-11).</summary>
public sealed class PermissionService
{
    private readonly IRoleRepository _roles;
    private readonly SessionService _session;
    private Dictionary<string, HashSet<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PermissionService(IRoleRepository roles, SessionService session)
    {
        _roles = roles;
        _session = session;
        Reload();
    }

    public event EventHandler? MatrixChanged;

    public void Reload()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in _roles.List())
            map[role.Code] = new HashSet<string>(_roles.PermissionsOf(role.Code), StringComparer.OrdinalIgnoreCase);
        _cache = map;
        MatrixChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RoleHas(string roleCode, string permission) =>
        _cache.TryGetValue(roleCode, out var set) && set.Contains(permission);

    /// <summary>True when a user is logged in and their role has the permission.</summary>
    public bool Has(string permission)
    {
        var user = _session.CurrentUser;
        if (user is null) return false;
        if (string.IsNullOrEmpty(permission)) return true;
        return RoleHas(user.RoleCode, permission);
    }

    public IReadOnlyCollection<string> PermissionsOf(string roleCode) =>
        _cache.TryGetValue(roleCode, out var set) ? set : Array.Empty<string>();

    /// <summary>Seeds roles and the default matrix on an empty database.</summary>
    public static void SeedDefaults(IRoleRepository roles)
    {
        if (roles.List().Count > 0) return;
        roles.Upsert(new Role { Code = Roles.Operator, NameVi = "Vận hành", NameEn = "Operator", IsSystem = true });
        roles.Upsert(new Role { Code = Roles.Supervisor, NameVi = "Giám sát", NameEn = "Supervisor", IsSystem = true });
        roles.Upsert(new Role { Code = Roles.Admin, NameVi = "Quản trị", NameEn = "Admin", IsSystem = true });
        foreach (var (role, perms) in Permissions.DefaultMatrix) roles.SetPermissions(role, perms);
    }
}
