using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Dialogs;

/// <summary>Modal, borderless, touch-sized dialog shell.</summary>
public class DialogWindow : Window
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
    private readonly ContentControl _body = new();

    public DialogWindow(string titleKey, double width = 640)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = width;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Theme.Surface;
        BorderBrush = Theme.TopBar;
        BorderThickness = new Thickness(2);
        FontFamily = Theme.Font;
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, this)) Owner = owner;

        var title = new Border { Background = Theme.TopBar, Padding = new Thickness(16, 10, 16, 10), Child = UiKit.Text(titleKey, Theme.FontLarge, true, Theme.TextOnDark) };
        title.MouseLeftButtonDown += (_, _) => DragMove();
        var root = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        var inner = new StackPanel { Margin = new Thickness(20) };
        inner.Children.Add(_body);
        inner.Children.Add(_buttons);
        root.Children.Add(inner);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
    }

    protected UIElement Body
    {
        set => _body.Content = value;
    }

    protected Button AddButton(string locKey, Action onClick, Brush? background = null)
    {
        var b = UiKit.Button(locKey, new RelayCommand(onClick), background, 160);
        _buttons.Children.Add(b);
        return b;
    }

    protected void Close(bool result)
    {
        DialogResult = result;
    }
}

public sealed class MessageDialog : DialogWindow
{
    public MessageDialog(string titleKey, string message, Brush accent, bool confirm = false) : base(titleKey)
    {
        Body = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(14, 6, 6, 6),
            Child = new TextBlock { Text = message, FontSize = Theme.FontLarge, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Text },
        };
        if (confirm)
        {
            AddButton("ui.cancel", () => Close(false), Theme.Neutral);
            AddButton("ui.confirm", () => Close(true));
        }
        else
        {
            AddButton("ui.ok", () => Close(true));
        }
    }
}

/// <summary>Asks for the reason of a GMP-relevant change (21 CFR 11.10(e) "why").</summary>
public sealed class ReasonDialog : DialogWindow
{
    private readonly TextBox _reason;
    public string Reason => _reason.Text.Trim();

    public ReasonDialog(string actionText) : base("ui.reasonTitle")
    {
        _reason = new TextBox { FontSize = Theme.FontNormal, Height = 120, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        TouchKeyboard.Attach(_reason);
        Body = UiKit.Column(new TextBlock { Text = actionText, FontSize = Theme.FontNormal, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold }, UiKit.Text("ui.reasonPrompt", Theme.FontSmall, color: Theme.TextMuted), _reason);
        AddButton("ui.cancel", () => Close(false), Theme.Neutral);
        var ok = AddButton("ui.confirm", () => { if (Reason.Length >= 3) Close(true); });
        Loaded += (_, _) => _reason.Focus();
    }
}

/// <summary>Electronic signature: user ID + password + displayed meaning (21 CFR 11.50 / 11.200).</summary>
public sealed class SignatureDialog : DialogWindow
{
    private readonly TextBox _user;
    private string _password = "";

    public SignatureInput Input => new(_user.Text.Trim(), _password);

    public SignatureDialog(string meaning, string? currentUser) : base("ui.signatureTitle", 680)
    {
        _user = new TextBox { Text = currentUser ?? "", FontSize = Theme.FontNormal, Height = Theme.InputHeight, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
        TouchKeyboard.Attach(_user);
        var pwd = UiKit.Password(p => _password = p);
        var meaningBox = new Border
        {
            Background = Theme.Brush("#EFF6FF"),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = meaning, FontSize = Theme.FontLarge, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
        };
        Body = UiKit.Column(
            UiKit.Text("ui.signatureMeaning", Theme.FontSmall, color: Theme.TextMuted),
            meaningBox,
            UiKit.Form(("ui.username", _user), ("ui.password", pwd)),
            UiKit.Text("ui.signatureLegal", Theme.FontSmall, color: Theme.TextMuted).Margin(0, 10, 0, 0));
        AddButton("ui.cancel", () => Close(false), Theme.Neutral);
        AddButton("ui.sign", () => Close(true), Theme.Success);
        Loaded += (_, _) => pwd.Focus();
    }
}

/// <summary>Bridges the ActionDispatcher prompts to WPF dialogs.</summary>
public sealed class WpfActionPrompt : IActionPrompt
{
    private readonly Func<string?> _currentUser;
    private readonly Func<string> _language;

    public WpfActionPrompt(Func<string?> currentUser, Func<string> language)
    {
        _currentUser = currentUser;
        _language = language;
    }

    public Task<string?> AskReasonAsync(ActionDefinition action)
    {
        var text = action.Text.Get(_language());
        var cut = text.IndexOf('{');
        var d = new ReasonDialog(cut > 0 ? text[..cut].Trim(' ', ':') : text);
        return Task.FromResult(d.ShowDialog() == true ? d.Reason : null);
    }

    public Task<SignatureInput?> AskSignatureAsync(ActionDefinition action, string meaning)
    {
        var d = new SignatureDialog(meaning, _currentUser());
        return Task.FromResult(d.ShowDialog() == true ? d.Input : null);
    }
}

public static class Dialogs
{
    public static void Info(string messageKey, params object[] args) =>
        new MessageDialog("ui.information", Loc.T(messageKey, args), Theme.Primary).ShowDialog();

    public static void Warn(string messageKey, params object[] args) =>
        new MessageDialog("ui.warning", Loc.T(messageKey, args), Theme.Warning).ShowDialog();

    public static bool Confirm(string messageKey, params object[] args) =>
        new MessageDialog("ui.confirmTitle", Loc.T(messageKey, args), Theme.Warning, confirm: true).ShowDialog() == true;

    public static void Error(Exception ex) =>
        new MessageDialog("ui.error", Loc.T("msg.unexpectedError", ex.Message), Theme.Danger).ShowDialog();

    /// <summary>Shows the result of an action; returns true on success. Cancel is silent.</summary>
    public static bool Show(ActionOutcome outcome, string? successKey = null, params object[] successArgs)
    {
        switch (outcome.Status)
        {
            case ActionStatus.Success:
                if (successKey is not null) Info(successKey, successArgs);
                return true;
            case ActionStatus.Cancelled:
                return false;
            case ActionStatus.SignatureFailed:
                new MessageDialog("ui.error", Loc.T("msg.signatureFailed") + "\n" + Loc.T(outcome.MessageKey, outcome.MessageArgs ?? Array.Empty<object>()), Theme.Danger).ShowDialog();
                return false;
            default:
                new MessageDialog(outcome.Status == ActionStatus.Denied ? "ui.warning" : "ui.error",
                    Loc.T(outcome.MessageKey, outcome.MessageArgs ?? Array.Empty<object>()), outcome.Status == ActionStatus.Denied ? Theme.Warning : Theme.Danger).ShowDialog();
                return false;
        }
    }
}
