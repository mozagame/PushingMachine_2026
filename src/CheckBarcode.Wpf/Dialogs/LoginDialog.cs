using System.Windows;
using System.Windows.Controls;
using CheckBarcode.Application.Security;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Dialogs;

/// <summary>Login with the forced password change flow (USR-02, USR-05, USR-06).</summary>
public sealed class LoginDialog : DialogWindow
{
    private readonly AuthService _auth;
    private readonly TextBox _user = new() { FontSize = Theme.FontNormal, Height = Theme.InputHeight, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
    private readonly TextBlock _message = new() { FontSize = Theme.FontNormal, Foreground = Theme.Danger, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 0) };
    private readonly StackPanel _loginPanel;
    private readonly StackPanel _changePanel;
    private readonly TextBlock _changeTitle = new() { FontSize = Theme.FontNormal, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 8) };
    private readonly Button _okButton;
    private string _password = "", _current = "", _new = "", _confirm = "";
    private bool _changeMode;

    public LoginDialog(AuthService auth, string? username = null, bool changePasswordOnly = false) : base("ui.loginTitle", 620)
    {
        _auth = auth;
        _user.Text = username ?? "";
        TouchKeyboard.Attach(_user);
        _loginPanel = UiKit.Column(UiKit.Form(("ui.username", _user), ("ui.password", UiKit.Password(p => _password = p))));
        _changePanel = UiKit.Column(_changeTitle);
        _changePanel.Visibility = Visibility.Collapsed;
        Body = UiKit.Column(_loginPanel, _changePanel, _message);
        AddButton("ui.cancel", () => Close(false), Theme.Neutral);
        _okButton = AddButton("ui.login", OnOk);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; OnOk(); } };
        Loaded += (_, _) => _user.Focus();
        if (changePasswordOnly) ShowChange(Loc.T("ui.changePasswordHint"));
    }

    private void ShowChange(string hint, bool currentKnown = false)
    {
        _changeMode = true;
        var form = currentKnown
            ? UiKit.Form(("ui.newPassword", UiKit.Password(p => _new = p)), ("ui.confirmPassword", UiKit.Password(p => _confirm = p)))
            : UiKit.Form(("ui.currentPassword", UiKit.Password(p => _current = p)), ("ui.newPassword", UiKit.Password(p => _new = p)), ("ui.confirmPassword", UiKit.Password(p => _confirm = p)));
        if (_changePanel.Children.Count > 1) _changePanel.Children.RemoveAt(1);
        _changePanel.Children.Add(form);
        _changeTitle.Text = hint;
        _loginPanel.Visibility = Visibility.Collapsed;
        _changePanel.Visibility = Visibility.Visible;
        _okButton.Content = new TextBlock { Text = Loc.T("ui.changePassword") };
        _message.Text = "";
    }

    private void OnOk()
    {
        if (_changeMode)
        {
            var r = _auth.ChangePassword(_user.Text, _current, _new, _confirm);
            if (!r.Ok)
            {
                _message.Text = Loc.T(r.MessageKey, r.Args ?? Array.Empty<object>());
                return;
            }
            Dialogs.Info("msg.passwordChanged");
            Close(false);
            return;
        }

        var result = _auth.Login(_user.Text, _password);
        switch (result.Status)
        {
            case LoginStatus.Success:
                Close(true);
                break;
            case LoginStatus.MustChangePassword:
                _current = _password;
                ShowChange(Loc.T(result.Expired ? "msg.passwordExpired" : "msg.mustChangePassword"), currentKnown: true);
                break;
            case LoginStatus.Locked:
                _message.Text = Loc.T("msg.accountLocked");
                break;
            case LoginStatus.Inactive:
                _message.Text = Loc.T("msg.accountInactive");
                break;
            default:
                _message.Text = result.RemainingAttempts > 0 ? Loc.T("msg.wrongPasswordRemaining", result.RemainingAttempts) : Loc.T("msg.invalidCredentials");
                break;
        }
    }
}
