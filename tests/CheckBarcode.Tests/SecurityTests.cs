using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;
using CheckBarcode.Tests.Framework;

namespace CheckBarcode.Tests;

public sealed class SecurityTests
{
    [Test("USR-12")]
    public void Passwords_are_hashed_with_salt()
    {
        var h = new Pbkdf2PasswordHasher(1000);
        var (hash1, salt1) = h.Hash("secret");
        var (hash2, salt2) = h.Hash("secret");
        Assert.True(hash1 != hash2 && salt1 != salt2, "salted");
        Assert.True(h.Verify("secret", hash1, salt1));
        Assert.False(h.Verify("Secret", hash1, salt1));
        Assert.False(hash1.Contains("secret"));
    }

    [Test("USR-01", "USR-05")]
    public async Task Seeded_admin_must_change_default_password()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        var r = env.Host.Auth.Login("admin", TestEnv.DefaultPassword);
        Assert.Equal(LoginStatus.MustChangePassword, r.Status);
        Assert.False(env.Host.Session.IsLoggedIn, "no session before password change");
        Assert.Equal("msg.passwordIsDefault", env.Host.Auth.ChangePassword("admin", TestEnv.DefaultPassword, TestEnv.DefaultPassword, TestEnv.DefaultPassword).MessageKey);
        Assert.Equal("msg.passwordMismatch", env.Host.Auth.ChangePassword("admin", TestEnv.DefaultPassword, "Abcdef1", "Abcdef2").MessageKey);
        env.LoginAdmin();
        Assert.True(env.Host.Session.IsLoggedIn);
        Assert.True(env.Audit(AuditCodes.PasswordChanged).Count == 1);
    }

    [Test("USR-02", "USR-03")]
    public async Task Five_wrong_passwords_lock_only_that_user()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.LoginAdmin();
        await env.Host.Users.CreateAsync("op2", "OP2", Roles.Operator);
        env.Host.Auth.Logout(LogoutReason.Manual);

        for (var i = 1; i <= 4; i++)
        {
            var r = env.Host.Auth.Login("op1", "wrong");
            Assert.Equal(LoginStatus.InvalidCredentials, r.Status);
            Assert.Equal(5 - i, r.RemainingAttempts);
        }
        Assert.Equal(LoginStatus.Locked, env.Host.Auth.Login("op1", "wrong").Status);
        Assert.Equal(LoginStatus.Locked, env.Host.Auth.Login("op1", "Pass@123").Status, "correct password still rejected");
        Assert.Equal(LoginStatus.MustChangePassword, env.Host.Auth.Login("op2", TestEnv.DefaultPassword).Status, "other user unaffected");

        var locked = env.Host.Users.LockedUsers().Single();
        Assert.Equal("op1", locked.Username);
        Assert.Equal(LockReason.WrongPassword, locked.LockReason);
        Assert.NotNull(locked.LockedUtc);
        Assert.Equal(5, env.Audit(AuditCodes.LoginFailed).Count(a => a.ObjectId == "op1"), "each failure audited");
        Assert.Equal(1, env.Audit(AuditCodes.AccountLocked).Count);
    }

    [Test("USR-04")]
    public async Task Admin_unlock_resets_to_default_password()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.Host.Auth.Logout(LogoutReason.Manual);
        for (var i = 0; i < 5; i++) env.Host.Auth.Login("op1", "bad");
        env.LoginAdmin();
        var user = env.Host.UsersRepo.FindByUsername("op1")!;
        Assert.True((await env.Host.Users.UnlockAsync(user.Id)).Ok);
        env.Host.Auth.Logout(LogoutReason.Manual);
        Assert.Equal(LoginStatus.InvalidCredentials, env.Host.Auth.Login("op1", "Pass@123").Status, "old password no longer valid");
        Assert.Equal(LoginStatus.MustChangePassword, env.Host.Auth.Login("op1", TestEnv.DefaultPassword).Status);
        var unlock = env.Audit("USER_UNLOCK").Single();
        Assert.Equal("admin", unlock.Username);
        Assert.Equal("test reason", unlock.Reason);
    }

    [Test("USR-04")]
    public async Task Admin_lock_records_reason_LockedByAdmin()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.LoginAdmin();
        var id = env.Host.UsersRepo.FindByUsername("op1")!.Id;
        Assert.True((await env.Host.Users.LockAsync(id)).Ok);
        Assert.Equal(LockReason.LockedByAdmin, env.Host.UsersRepo.FindById(id)!.LockReason);
        var self = await env.Host.Users.LockAsync(env.Host.Session.CurrentUser!.Id);
        Assert.Equal("msg.cannotLockSelf", self.MessageKey);
    }

    [Test("USR-06")]
    public async Task Password_expires_after_two_months_except_admin()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.Host.Auth.Logout(LogoutReason.Manual);
        env.Clock.Advance(TimeSpan.FromDays(58));
        Assert.Equal(LoginStatus.Success, env.Host.Auth.Login("op1", "Pass@123").Status);
        env.Host.Auth.Logout(LogoutReason.Manual);
        env.Clock.Advance(TimeSpan.FromDays(5));
        var r = env.Host.Auth.Login("op1", "Pass@123");
        Assert.Equal(LoginStatus.MustChangePassword, r.Status);
        Assert.True(r.Expired);
        Assert.Equal("msg.passwordReused", env.Host.Auth.ChangePassword("op1", "Pass@123", "Pass@123", "Pass@123").MessageKey);
        Assert.True(env.Host.Auth.ChangePassword("op1", "Pass@123", "Next@456", "Next@456").Ok);
        Assert.Equal(LoginStatus.Success, env.Host.Auth.Login("op1", "Next@456").Status);
        env.Host.Auth.Logout(LogoutReason.Manual);
        Assert.Equal(LoginStatus.Success, env.Host.Auth.Login("admin", TestEnv.AdminPassword).Status, "admin exempt from expiry");
    }

    [Test("USR-05")]
    public async Task Changing_password_ends_the_session()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        Assert.True(env.Host.Session.IsLoggedIn);
        Assert.True(env.Host.Auth.ChangePassword("op1", "Pass@123", "Other@789", "Other@789").Ok);
        Assert.False(env.Host.Session.IsLoggedIn, "logged out after change");
    }

    [Test("USR-07")]
    public async Task Idle_logout_keeps_last_user_for_attribution()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(env.Host.Auth.CheckIdle());
        env.Clock.Advance(TimeSpan.FromMinutes(1.5));
        Assert.True(env.Host.Auth.CheckIdle());
        Assert.False(env.Host.Session.IsLoggedIn);
        Assert.Equal(1, env.Audit(AuditCodes.LogoutIdle).Count);

        var rec = env.Host.Audit.Write("TEST_EVENT");
        Assert.Equal("op1", rec.Username);
        Assert.Equal(SessionState.Expired, rec.SessionState);
    }

    [Test("USR-09", "AUD-04")]
    public async Task Actions_without_login_are_denied_and_audited()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        var outcome = await env.Host.Batches.CreateAsync("P", "L1", null, null);
        Assert.Equal(ActionStatus.Denied, outcome.Status);
        Assert.Equal("msg.loginRequired", outcome.MessageKey);
        var denied = env.Audit(AuditCodes.ActionDenied).Single();
        Assert.Contains("BATCH_CREATE", denied.MessageArgs);
        Assert.Equal("SYSTEM", denied.Username);
    }

    [Test("USR-11")]
    public async Task Operator_cannot_manage_recipes_or_users()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        var r = await env.Host.Recipes.CreateAsync(new Application.Recipes.RecipeInput("X", CameraRole.Leaflet, "1", BarcodeFormat.Pharmacode, null, null, null, null));
        Assert.Equal(ActionStatus.Denied, r.Status);
        Assert.Equal("msg.permissionDenied", r.MessageKey);
        Assert.Equal(ActionStatus.Denied, (await env.Host.Users.CreateAsync("x1", "x", Roles.Operator)).Status);
        Assert.False(env.Host.Permissions.Has(Permissions.SystemExit), "operator cannot exit to desktop");
    }

    [Test("USR-11")]
    public async Task Permission_matrix_change_is_audited_per_permission()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        env.LoginAdmin();
        var perms = env.Host.Permissions.PermissionsOf(Roles.Operator).Append(Permissions.RecipeManage).Where(p => p != Permissions.ImageView).ToList();
        Assert.True((await env.Host.Users.SetRolePermissionsAsync(Roles.Operator, perms)).Ok);
        Assert.True(env.Host.Permissions.RoleHas(Roles.Operator, Permissions.RecipeManage));
        var changes = env.Audit("ROLE_PERMISSIONS_CHANGE");
        Assert.Equal(2, changes.Count);
        Assert.True(changes.Any(c => c.Field == Permissions.RecipeManage && c.NewValue == "true"));
        Assert.True(changes.Any(c => c.Field == Permissions.ImageView && c.NewValue == "false"));
        var bad = await env.Host.Users.SetRolePermissionsAsync(Roles.Admin, new[] { Permissions.BatchRun });
        Assert.Equal("msg.adminMustKeepUserManage", bad.MessageKey);
    }

    [Test("USR-10", "USR-01")]
    public async Task Deactivated_username_cannot_be_reused_or_login()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.LoginAdmin();
        var id = env.Host.UsersRepo.FindByUsername("op1")!.Id;
        Assert.True((await env.Host.Users.DeactivateAsync(id)).Ok);
        Assert.Equal("msg.usernameExists", (await env.Host.Users.CreateAsync("OP1", "dup", Roles.Operator)).MessageKey);
        env.Host.Auth.Logout(LogoutReason.Manual);
        Assert.Equal(LoginStatus.Inactive, env.Host.Auth.Login("op1", "Pass@123").Status);
        Assert.Equal(1, env.Audit("USER_DEACTIVATE").Count);
    }

    [Test("USR-01")]
    public async Task Last_admin_cannot_be_removed()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        env.LoginAdmin();
        await env.Host.Users.CreateAsync("sup", "S", Roles.Supervisor);
        var admin = env.Host.Session.CurrentUser!;
        Assert.Equal("msg.cannotDeactivateSelf", (await env.Host.Users.DeactivateAsync(admin.Id)).MessageKey);
        Assert.Equal("msg.cannotChangeOwnRole", (await env.Host.Users.UpdateAsync(admin.Id, "x", Roles.Operator)).MessageKey);
    }

    [Test("USR-01", "AUD-02")]
    public async Task User_changes_are_audited_field_by_field()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        env.LoginAdmin();
        await env.Host.Users.CreateAsync("op9", "Nguyen Van A", Roles.Operator);
        var id = env.Host.UsersRepo.FindByUsername("op9")!.Id;
        Assert.True((await env.Host.Users.UpdateAsync(id, "Nguyễn Văn A", Roles.Supervisor)).Ok);
        var upd = env.Audit("USER_UPDATE");
        Assert.Equal(2, upd.Count);
        var role = upd.Single(u => u.Field == "RoleCode");
        Assert.Equal(Roles.Operator, role.OldValue);
        Assert.Equal(Roles.Supervisor, role.NewValue);
        Assert.Equal("op9", role.ObjectId);
        Assert.True((await env.Host.Users.ResetPasswordAsync(id)).Ok);
        Assert.Equal(1, env.Audit("USER_RESET_PASSWORD").Count);
    }

    [Test("AUD-04")]
    public async Task Signature_is_required_verified_and_linked()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        env.LoginAdmin();
        await env.CreateRecipesAsync();
        var recipe = env.Host.Recipes.List(CameraRole.Leaflet).Single();
        var input = new Application.Recipes.RecipeInput(recipe.Name, CameraRole.Leaflet, "999", BarcodeFormat.Pharmacode, null, null, null, null);

        env.Prompt.Signature = null;
        Assert.Equal(ActionStatus.Cancelled, (await env.Host.Recipes.EditAsync(recipe.Id, input)).Status);

        env.Prompt.Signature = new SignatureInput("admin", "wrong");
        var bad = await env.Host.Recipes.EditAsync(recipe.Id, input);
        Assert.Equal(ActionStatus.SignatureFailed, bad.Status);
        Assert.Equal(1, env.Audit(AuditCodes.SignatureFailed).Count);
        Assert.Equal("767", env.Host.Recipes.Find(recipe.Id)!.Barcode, "not changed after bad signature");

        env.Prompt.Signature = new SignatureInput("someoneelse", TestEnv.AdminPassword);
        Assert.Equal("msg.signerMustBeCurrentUser", (await env.Host.Recipes.EditAsync(recipe.Id, input)).MessageKey);

        env.Prompt.Signature = new SignatureInput("admin", TestEnv.AdminPassword);
        Assert.True((await env.Host.Recipes.EditAsync(recipe.Id, input)).Ok);
        var signed = env.Audit("RECIPE_EDIT").Where(a => a.SignatureMeaning is not null).ToList();
        Assert.True(signed.Count >= 2, "every change record signed");
        Assert.True(env.Host.AuditStore.Verify().Ok);
    }

    [Test("USR-02")]
    public async Task Failed_signatures_count_towards_lockout()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("sup1", Roles.Supervisor);
        for (var i = 0; i < 5; i++) env.Host.Auth.VerifySignature("sup1", "nope");
        Assert.Equal(UserStatus.Locked, env.Host.UsersRepo.FindByUsername("sup1")!.Status);
        Assert.False(env.Host.Session.IsLoggedIn, "session closed when locked by signature failures");
    }

    [Test("USR-07", "SYS")]
    public async Task Security_policy_changes_are_audited_and_applied()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        env.LoginAdmin();
        var p = new SecurityPolicy { MaxFailedLogins = 3, IdleLogoutMinutes = 10, PasswordExpiryMonths = 2, PasswordMinLength = 8, PasswordHistoryCount = 3, DefaultPassword = "Start#2026" };
        Assert.True((await env.Host.Users.SavePolicyAsync(p)).Ok);
        Assert.Equal(3, env.Host.Policy.MaxFailedLogins);
        var changes = env.Audit("SECURITY_POLICY_CHANGE");
        Assert.True(changes.Any(c => c.Field == SecurityPolicy.KeyMaxFailed && c.OldValue == "5" && c.NewValue == "3"));
        Assert.False(changes.Any(c => c.NewValue.Contains("Start#2026")), "default password never stored in clear in the audit");
    }
}
