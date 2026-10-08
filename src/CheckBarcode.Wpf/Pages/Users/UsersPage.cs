using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Users;

public sealed class UserRow
{
    public UserRow(User u, AppHost host)
    {
        User = u;
        Role = Loc.T("value." + u.RoleCode);
        Status = Loc.T("value." + u.Status);
        LockReason = u.LockReason == Domain.LockReason.None ? "" : Loc.T("ui.lockReason." + u.LockReason);
        LockedSince = host.Time.DateTime(u.LockedUtc);
        PasswordChanged = host.Time.DateTime(u.PasswordChangedUtc);
    }

    public User User { get; }
    public string Username => User.Username;
    public string FullName => User.FullName;
    public string Role { get; }
    public string Status { get; }
    public string LockReason { get; }
    public string LockedSince { get; }
    public string PasswordChanged { get; }
}

public sealed record RoleOption(string Code, string Display)
{
    public override string ToString() => Display;
}

public sealed class PermissionRow : ObservableObject
{
    private bool _granted;
    public PermissionRow(string code, bool granted)
    {
        Code = code;
        _granted = granted;
    }

    public string Code { get; }
    public string Text => Loc.T("perm." + Code);
    public bool Granted { get => _granted; set => Set(ref _granted, value); }
}

/// <summary>User administration, locked users, permission matrix and security policy (USR-01, 03, 04, 06, 07, 10, 11).</summary>
public sealed class UsersViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;
    private UserRow? _selected;
    private bool _onlyLocked;
    private string _username = "", _fullName = "";
    private RoleOption? _role;
    private RoleOption? _matrixRole;
    private string _maxFailed = "", _idle = "", _expiry = "", _minLength = "", _history = "", _defaultPassword = "";

    public UsersViewModel(AppHost host)
    {
        _host = host;
        NewCommand = new RelayCommand(ClearForm);
        SaveCommand = new AsyncCommand(SaveAsync);
        ResetPasswordCommand = new AsyncCommand(() => Run(id => host.Users.ResetPasswordAsync(id), "ui.resetPasswordConfirm"), () => Selected is not null);
        LockCommand = new AsyncCommand(() => Run(id => host.Users.LockAsync(id), "ui.lockConfirm"), () => Selected?.User.Status == UserStatus.Active);
        UnlockCommand = new AsyncCommand(() => Run(id => host.Users.UnlockAsync(id), "ui.unlockConfirm"), () => Selected?.User.Status == UserStatus.Locked);
        DeactivateCommand = new AsyncCommand(() => Run(id => host.Users.DeactivateAsync(id), "ui.deactivateConfirm"), () => Selected is { User.Status: not UserStatus.Inactive });
        ReactivateCommand = new AsyncCommand(() => Run(id => host.Users.ReactivateAsync(id), "ui.reactivateConfirm"), () => Selected?.User.Status == UserStatus.Inactive);
        SavePermissionsCommand = new AsyncCommand(SavePermissionsAsync, () => MatrixRole is not null);
        SavePolicyCommand = new AsyncCommand(SavePolicyAsync);
        host.Localizer.LanguageChanged += (_, _) => OnUi(OnShown);
    }

    public ObservableCollection<UserRow> Rows { get; } = new();
    public ObservableCollection<RoleOption> Roles { get; } = new();
    public ObservableCollection<PermissionRow> PermissionRows { get; } = new();
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetPasswordCommand { get; }
    public ICommand LockCommand { get; }
    public ICommand UnlockCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand ReactivateCommand { get; }
    public ICommand SavePermissionsCommand { get; }
    public ICommand SavePolicyCommand { get; }

    public bool OnlyLocked { get => _onlyLocked; set { if (Set(ref _onlyLocked, value)) LoadUsers(); } }
    public bool IsNew => Selected is null;
    public string Username { get => _username; set => Set(ref _username, value); }
    public string FullName { get => _fullName; set => Set(ref _fullName, value); }
    public RoleOption? Role { get => _role; set => Set(ref _role, value); }
    public string MaxFailed { get => _maxFailed; set => Set(ref _maxFailed, value); }
    public string IdleMinutes { get => _idle; set => Set(ref _idle, value); }
    public string ExpiryMonths { get => _expiry; set => Set(ref _expiry, value); }
    public string MinLength { get => _minLength; set => Set(ref _minLength, value); }
    public string HistoryCount { get => _history; set => Set(ref _history, value); }
    public string DefaultPassword { get => _defaultPassword; set => Set(ref _defaultPassword, value); }

    public UserRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(IsNew));
            if (value is null) return;
            Username = value.Username;
            FullName = value.FullName;
            Role = Roles.FirstOrDefault(r => r.Code == value.User.RoleCode);
        }
    }

    public RoleOption? MatrixRole
    {
        get => _matrixRole;
        set { if (Set(ref _matrixRole, value)) LoadMatrix(); }
    }

    public void OnShown()
    {
        Roles.Clear();
        foreach (var r in _host.Users.Roles()) Roles.Add(new RoleOption(r.Code, _host.Localizer.Language == "en" ? r.NameEn : r.NameVi));
        MatrixRole ??= Roles.FirstOrDefault();
        LoadUsers();
        LoadMatrix();
        LoadPolicy();
    }

    private void LoadUsers()
    {
        Rows.Clear();
        foreach (var u in _host.Users.List().Where(u => !OnlyLocked || u.Status == UserStatus.Locked)) Rows.Add(new UserRow(u, _host));
    }

    private void LoadMatrix()
    {
        PermissionRows.Clear();
        if (MatrixRole is null) return;
        var granted = _host.Permissions.PermissionsOf(MatrixRole.Code);
        foreach (var p in Permissions.All) PermissionRows.Add(new PermissionRow(p, granted.Contains(p)));
    }

    private void LoadPolicy()
    {
        var p = _host.Policy;
        MaxFailed = p.MaxFailedLogins.ToString(CultureInfo.InvariantCulture);
        IdleMinutes = p.IdleLogoutMinutes.ToString(CultureInfo.InvariantCulture);
        ExpiryMonths = p.PasswordExpiryMonths.ToString(CultureInfo.InvariantCulture);
        MinLength = p.PasswordMinLength.ToString(CultureInfo.InvariantCulture);
        HistoryCount = p.PasswordHistoryCount.ToString(CultureInfo.InvariantCulture);
        DefaultPassword = p.DefaultPassword;
    }

    private void ClearForm()
    {
        Selected = null;
        Username = FullName = "";
        Role = Roles.FirstOrDefault(r => r.Code == Domain.Roles.Operator);
    }

    private async Task SaveAsync()
    {
        if (Role is null) return;
        var outcome = Selected is null
            ? await _host.Users.CreateAsync(Username, FullName, Role.Code)
            : await _host.Users.UpdateAsync(Selected.User.Id, FullName, Role.Code);
        if (Dialogs.Dialogs.Show(outcome, Selected is null ? "ui.userCreated" : "msg.saved", _host.Policy.DefaultPassword))
        {
            LoadUsers();
            ClearForm();
        }
    }

    private async Task Run(Func<long, Task<Application.Actions.ActionOutcome>> action, string confirmKey)
    {
        if (Selected is null || !Dialogs.Dialogs.Confirm(confirmKey, Selected.Username)) return;
        if (Dialogs.Dialogs.Show(await action(Selected.User.Id), "msg.saved"))
        {
            LoadUsers();
            ClearForm();
        }
    }

    private async Task SavePermissionsAsync()
    {
        if (MatrixRole is null) return;
        Dialogs.Dialogs.Show(await _host.Users.SetRolePermissionsAsync(MatrixRole.Code, PermissionRows.Where(p => p.Granted).Select(p => p.Code)), "msg.saved");
        LoadMatrix();
    }

    private async Task SavePolicyAsync()
    {
        static int I(string s) => int.TryParse(s, out var v) ? v : -1;
        var policy = new SecurityPolicy
        {
            MaxFailedLogins = I(MaxFailed),
            IdleLogoutMinutes = double.TryParse(IdleMinutes, NumberStyles.Float, CultureInfo.InvariantCulture, out var idle) ? idle : -1,
            PasswordExpiryMonths = I(ExpiryMonths),
            PasswordMinLength = I(MinLength),
            PasswordHistoryCount = I(HistoryCount),
            DefaultPassword = DefaultPassword,
        };
        Dialogs.Dialogs.Show(await _host.Users.SavePolicyAsync(policy), "msg.saved");
        LoadPolicy();
    }
}

public sealed class UsersView : UserControl
{
    public UsersView(UsersViewModel vm)
    {
        DataContext = vm;
        var tabs = new TabControl { FontSize = Theme.FontNormal };
        tabs.Items.Add(Tab("ui.tab.users", UsersTab()));
        tabs.Items.Add(Tab("ui.tab.permissions", PermissionsTab()));
        tabs.Items.Add(Tab("ui.tab.policy", PolicyTab()));
        Content = UiKit.Card(tabs);
    }

    private static TabItem Tab(string key, UIElement content)
    {
        var header = UiKit.Text(key, Theme.FontNormal, true).Margin(12, 6, 12, 6);
        return new TabItem { Header = header, Content = content };
    }

    private static UIElement UsersTab()
    {
        var grid = UiKit.Table(nameof(UsersViewModel.Rows), ("ui.username", nameof(UserRow.Username), 1), ("ui.fullName", nameof(UserRow.FullName), 1.4),
            ("col.role", nameof(UserRow.Role), 0.8), ("col.status", nameof(UserRow.Status), 0.8), ("ui.lockReasonCol", nameof(UserRow.LockReason), 1.1),
            ("ui.lockedSince", nameof(UserRow.LockedSince), 1.1), ("ui.passwordChanged", nameof(UserRow.PasswordChanged), 1.1));
        grid.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(UsersViewModel.Selected)) { Mode = BindingMode.TwoWay });
        var onlyLocked = UiKit.Check("ui.onlyLocked", nameof(UsersViewModel.OnlyLocked));
        var list = new DockPanel();
        DockPanel.SetDock(onlyLocked, Dock.Top);
        list.Children.Add(onlyLocked);
        list.Children.Add(grid);

        var username = UiKit.TextBox(nameof(UsersViewModel.Username)).Bind(IsEnabledProperty, nameof(UsersViewModel.IsNew));
        var form = UiKit.Form(("ui.username", username), ("ui.fullName", UiKit.TextBox(nameof(UsersViewModel.FullName))),
            ("col.role", UiKit.Combo(nameof(UsersViewModel.Roles), nameof(UsersViewModel.Role), nameof(RoleOption.Display))));
        var buttons = UiKit.Wrap(
            UiKit.Button("ui.new", null!, Theme.Neutral, 170).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.NewCommand)),
            UiKit.Button("ui.save", null!, Theme.Success, 170).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.SaveCommand)),
            UiKit.Button("ui.resetPassword", null!, Theme.Warning, 230).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.ResetPasswordCommand)),
            UiKit.Button("ui.lock", null!, Theme.Danger, 170).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.LockCommand)),
            UiKit.Button("ui.unlock", null!, Theme.Primary, 170).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.UnlockCommand)),
            UiKit.Button("ui.deactivate", null!, Theme.Danger, 200).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.DeactivateCommand)),
            UiKit.Button("ui.reactivate", null!, Theme.Primary, 200).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.ReactivateCommand)));
        var editor = UiKit.Column(form, buttons, UiKit.Text("ui.userHint", Theme.FontSmall, color: Theme.TextMuted).Margin(4, 10, 4, 0));

        var root = UiKit.Layout("*,600");
        root.Add(list.At(0, 0), UiKit.Card(editor).At(0, 1));
        return root;
    }

    private static UIElement PermissionsTab()
    {
        var role = UiKit.Combo(nameof(UsersViewModel.Roles), nameof(UsersViewModel.MatrixRole), nameof(RoleOption.Display), 300);
        var list = new ItemsControl();
        list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(UsersViewModel.PermissionRows)));
        var template = new DataTemplate(typeof(PermissionRow));
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetValue(MarginProperty, new Thickness(6));
        check.SetValue(FontSizeProperty, Theme.FontNormal);
        check.SetValue(LayoutTransformProperty, new System.Windows.Media.ScaleTransform(1.25, 1.25));
        check.SetBinding(ContentControl.ContentProperty, new Binding(nameof(PermissionRow.Text)));
        check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(PermissionRow.Granted)) { Mode = BindingMode.TwoWay });
        template.VisualTree = check;
        list.ItemTemplate = template;
        var save = UiKit.Button("ui.savePermissions", null!, Theme.Success, 260).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.SavePermissionsCommand));
        return UiKit.Column(UiKit.Row(UiKit.Text("col.role"), role, save), new ScrollViewer { Content = list, Height = 760, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private static UIElement PolicyTab()
    {
        var form = UiKit.Form(
            ("field.Security.MaxFailedLogins", UiKit.TextBox(nameof(UsersViewModel.MaxFailed), 160)),
            ("field.Security.IdleLogoutMinutes", UiKit.TextBox(nameof(UsersViewModel.IdleMinutes), 160)),
            ("field.Security.PasswordExpiryMonths", UiKit.TextBox(nameof(UsersViewModel.ExpiryMonths), 160)),
            ("field.Security.PasswordMinLength", UiKit.TextBox(nameof(UsersViewModel.MinLength), 160)),
            ("field.Security.PasswordHistoryCount", UiKit.TextBox(nameof(UsersViewModel.HistoryCount), 160)),
            ("field.Security.DefaultPassword", UiKit.TextBox(nameof(UsersViewModel.DefaultPassword), 260)));
        form.MaxWidth = 760;
        form.HorizontalAlignment = HorizontalAlignment.Left;
        var save = UiKit.Button("ui.save", null!, Theme.Success, 200).Bind(ButtonBase.CommandProperty, nameof(UsersViewModel.SavePolicyCommand));
        save.HorizontalAlignment = HorizontalAlignment.Left;
        return UiKit.Column(form, save, UiKit.Text("ui.policyHint", Theme.FontSmall, color: Theme.TextMuted).Margin(4, 10, 4, 0));
    }
}
