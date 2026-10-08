using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Shell;

/// <summary>Full-screen kiosk shell: top bar, navigation, page area, alarm banner (SYS-02, SYS-05).</summary>
public sealed class MainWindow : Window
{
    /// <summary>Set by the exit command; any other close attempt (Alt+F4) is refused (kiosk, USR-08).</summary>
    public static bool AllowClose { get; set; }

    public MainWindow(ShellViewModel vm)
    {
        DataContext = vm;
        Title = "CheckBarcode v2";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Width = 1920;
        Height = 1080;
        Background = Theme.Background;
        FontFamily = Theme.Font;
        Closing += (_, e) => { if (!AllowClose) e.Cancel = true; };

        var root = UiKit.Layout("230,*", "72,*,56");
        root.Add(TopBar().At(0, 0, 1, 2), Navigation().At(1, 0), Page().At(1, 1), AlarmBanner().At(2, 0, 1, 2));
        Content = root;
    }

    private static UIElement TopBar()
    {
        var bar = new DockPanel { Background = Theme.TopBar, LastChildFill = false };
        var title = UiKit.Value(nameof(ShellViewModel.MachineTitle), Theme.FontLarge, true, Theme.TextOnDark).Margin(20, 0, 20, 0);
        DockPanel.SetDock(title, Dock.Left);
        bar.Children.Add(title);

        var clock = UiKit.Value(nameof(ShellViewModel.Clock), Theme.FontLarge, true, Theme.TextOnDark).Margin(20, 0, 20, 0);
        DockPanel.SetDock(clock, Dock.Left);
        bar.Children.Add(clock);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(UiKit.Pill(nameof(ShellViewModel.PlcText), nameof(ShellViewModel.PlcBrush)));
        right.Children.Add(UiKit.Pill(nameof(ShellViewModel.MachineText), nameof(ShellViewModel.MachineBrush)));
        right.Children.Add(UiKit.Value(nameof(ShellViewModel.DiskText), Theme.FontSmall, color: Theme.TextOnDark).Margin(10, 0, 10, 0));
        var user = UiKit.Column(UiKit.Value(nameof(ShellViewModel.UserText), Theme.FontNormal, true, Theme.TextOnDark), UiKit.Value(nameof(ShellViewModel.RoleText), Theme.FontSmall, color: Theme.TextOnDark));
        user.Margin = new Thickness(16, 0, 8, 0);
        user.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(user);
        right.Children.Add(SmallButton(nameof(ShellViewModel.LanguageText), nameof(ShellViewModel.LanguageCommand)));
        var login = UiKit.Button("ui.login", null!, Theme.Primary, 140);
        login.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(nameof(ShellViewModel.LoginCommand)));
        ((TextBlock)login.Content).SetBinding(TextBlock.TextProperty, new Binding(nameof(ShellViewModel.LoginKey)) { Converter = new FuncConverter(k => Loc.T((string)k!)), Mode = BindingMode.OneWay });
        right.Children.Add(login);
        var pwd = UiKit.Button("ui.changePassword", null!, Theme.Neutral, 150);
        pwd.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(nameof(ShellViewModel.ChangePasswordCommand)));
        pwd.SetBinding(VisibilityProperty, new Binding(nameof(ShellViewModel.IsLoggedIn)) { Converter = FuncConverter.BoolToVisible });
        right.Children.Add(pwd);
        var exit = UiKit.Button("ui.exit", null!, Theme.Danger, 120);
        exit.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(nameof(ShellViewModel.ExitCommand)));
        exit.SetBinding(VisibilityProperty, new Binding(nameof(ShellViewModel.CanExit)) { Converter = FuncConverter.BoolToVisible });
        right.Children.Add(exit);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        return bar;
    }

    private static Button SmallButton(string textPath, string commandPath)
    {
        var b = UiKit.Button("ui.language", null!, Theme.Nav, 110);
        ((TextBlock)b.Content).SetBinding(TextBlock.TextProperty, new Binding(textPath));
        b.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(commandPath));
        return b;
    }

    private static UIElement Navigation()
    {
        var list = new ItemsControl { Background = Theme.Nav, Padding = new Thickness(0, 10, 0, 0) };
        list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ShellViewModel.Pages)));
        var template = new DataTemplate(typeof(NavItem));
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(HeightProperty, 64.0);
        button.SetValue(MarginProperty, new Thickness(8, 4, 8, 4));
        button.SetValue(HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        button.SetValue(FontSizeProperty, Theme.FontNormal);
        button.SetValue(ForegroundProperty, Theme.TextOnDark);
        button.SetValue(BorderThicknessProperty, new Thickness(0));
        button.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding("DataContext.NavigateCommand") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1) });
        button.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandParameterProperty, new Binding());
        button.SetBinding(IsEnabledProperty, new Binding(nameof(NavItem.IsEnabled)));
        button.SetBinding(BackgroundProperty, new Binding(nameof(NavItem.IsSelected)) { Converter = new FuncConverter(v => v is true ? Theme.NavSelected : Theme.Nav) });
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.MarginProperty, new Thickness(14, 0, 0, 0));
        text.SetBinding(TextBlock.TextProperty, new MultiBinding
        {
            Converter = new NavTextConverter(),
            Bindings = { new Binding(nameof(NavItem.Icon)), new Binding(nameof(NavItem.TitleKey)), new Binding("[ui.login]") { Source = Loc.Instance } },
        });
        button.AppendChild(text);
        template.VisualTree = button;
        list.ItemTemplate = template;
        return list;
    }

    private static UIElement Page()
    {
        var host = new ContentControl { Margin = new Thickness(8) };
        host.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ShellViewModel.CurrentView)));
        return host;
    }

    private static UIElement AlarmBanner()
    {
        var banner = new Border { Background = Theme.AlarmBanner, BorderBrush = Theme.Danger, BorderThickness = new Thickness(0, 2, 0, 0), Cursor = Cursors.Hand };
        banner.SetBinding(VisibilityProperty, new Binding(nameof(ShellViewModel.HasAlarm)) { Converter = FuncConverter.BoolToVisible });
        var dock = new DockPanel();
        var go = UiKit.Button("ui.openAlarms", null!, Theme.Danger, 180);
        go.Height = 44;
        go.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(nameof(ShellViewModel.AlarmsCommand)));
        DockPanel.SetDock(go, Dock.Right);
        dock.Children.Add(go);
        dock.Children.Add(UiKit.Value(nameof(ShellViewModel.AlarmText), Theme.FontLarge, true, Theme.Danger).Margin(20, 0, 0, 0));
        banner.Child = dock;
        return banner;
    }

    private sealed class NavTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            $"{values[0]}   {Loc.T((string)values[1])}";

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
