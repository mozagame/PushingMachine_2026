using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CheckBarcode.Wpf.Ui;

/// <summary>Value converter from a lambda.</summary>
public sealed class FuncConverter : IValueConverter
{
    private readonly Func<object?, object?> _convert;
    public FuncConverter(Func<object?, object?> convert) => _convert = convert;
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => _convert(value);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    public static readonly FuncConverter BoolToVisible = new(v => v is true ? Visibility.Visible : Visibility.Collapsed);
    public static readonly FuncConverter BoolToCollapsed = new(v => v is true ? Visibility.Collapsed : Visibility.Visible);
    public static readonly FuncConverter NotNullToVisible = new(v => v is null || v is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible);
    public static readonly FuncConverter OnlineBrush = new(v => v is true ? Theme.Success : Theme.Danger);
    public static readonly FuncConverter Not = new(v => v is not true);
}

/// <summary>
/// Small factory for touch-friendly controls built in code. Views stay declarative:
/// <c>Ui.Button("ui.save", vm.SaveCommand)</c>, <c>Ui.TextBox(nameof(vm.Name))</c>.
/// </summary>
public static class UiKit
{
    public static T With<T>(this T element, Action<T> configure) where T : DependencyObject
    {
        configure(element);
        return element;
    }

    public static T Bind<T>(this T element, DependencyProperty property, string path, IValueConverter? converter = null, BindingMode mode = BindingMode.OneWay) where T : FrameworkElement
    {
        element.SetBinding(property, new Binding(path) { Converter = converter, Mode = mode, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return element;
    }

    public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    public static T Margin<T>(this T element, double all) where T : FrameworkElement
    {
        element.Margin = new Thickness(all);
        return element;
    }

    public static T At<T>(this T element, int row, int column = 0, int rowSpan = 1, int columnSpan = 1) where T : UIElement
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        if (rowSpan > 1) Grid.SetRowSpan(element, rowSpan);
        if (columnSpan > 1) Grid.SetColumnSpan(element, columnSpan);
        return element;
    }

    /// <summary>Localized static text.</summary>
    public static TextBlock Text(string locKey, double size = Theme.FontNormal, bool bold = false, Brush? color = null)
    {
        var t = new TextBlock { FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? Theme.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.SetBinding(TextBlock.TextProperty, Loc.Bind(locKey));
        return t;
    }

    /// <summary>Text bound to a view-model property.</summary>
    public static TextBlock Value(string path, double size = Theme.FontNormal, bool bold = false, Brush? color = null, string? format = null)
    {
        var t = new TextBlock { FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? Theme.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.SetBinding(TextBlock.TextProperty, new Binding(path) { StringFormat = format, Mode = BindingMode.OneWay });
        return t;
    }

    public static Button Button(string locKey, ICommand command, Brush? background = null, double width = double.NaN, object? parameter = null)
    {
        var b = new Button
        {
            Height = Theme.ButtonHeight,
            MinWidth = 120,
            Width = width,
            Padding = new Thickness(18, 4, 18, 4),
            Margin = new Thickness(4),
            FontSize = Theme.FontNormal,
            Foreground = Theme.TextOnDark,
            Background = background ?? Theme.Primary,
            BorderThickness = new Thickness(0),
            Command = command,
            CommandParameter = parameter,
            Cursor = Cursors.Hand,
            // Touch UI: buttons never take keyboard focus, so an Enter key that closes a dialog
            // cannot also "click" the button behind it (e.g. Login immediately followed by Logout).
            Focusable = false,
        };
        var text = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        text.SetBinding(TextBlock.TextProperty, Loc.Bind(locKey));
        b.Content = text;
        b.Template = RoundedTemplate();
        return b;
    }

    private static ControlTemplate RoundedTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        t.VisualTree = border;
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Disabled, "bd"));
        t.Triggers.Add(disabled);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75, "bd"));
        t.Triggers.Add(pressed);
        return t;
    }

    public static TextBox TextBox(string path, double width = double.NaN, bool readOnly = false)
    {
        var tb = new TextBox
        {
            Height = Theme.InputHeight,
            Width = width,
            FontSize = Theme.FontNormal,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(4),
            IsReadOnly = readOnly,
        };
        tb.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        if (!readOnly) TouchKeyboard.Attach(tb);
        return tb;
    }

    public static PasswordBox Password(Action<string> onChanged)
    {
        var pb = new PasswordBox { Height = Theme.InputHeight, FontSize = Theme.FontNormal, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4), Padding = new Thickness(6, 0, 6, 0) };
        pb.PasswordChanged += (_, _) => onChanged(pb.Password);
        TouchKeyboard.Attach(pb);
        return pb;
    }

    public static ComboBox Combo(string itemsPath, string selectedPath, string? displayPath = null, double width = double.NaN)
    {
        var cb = new ComboBox { Height = Theme.InputHeight, Width = width, FontSize = Theme.FontNormal, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center, DisplayMemberPath = displayPath };
        cb.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(itemsPath));
        cb.SetBinding(Selector.SelectedItemProperty, new Binding(selectedPath) { Mode = BindingMode.TwoWay });
        return cb;
    }

    public static CheckBox Check(string locKey, string path)
    {
        var c = new CheckBox { FontSize = Theme.FontNormal, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center, LayoutTransform = new ScaleTransform(1.3, 1.3) };
        c.SetBinding(ContentControl.ContentProperty, Loc.Bind(locKey));
        c.SetBinding(ToggleButton.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        return c;
    }

    public static DatePicker Date(string path)
    {
        var d = new DatePicker { FontSize = Theme.FontNormal, Height = Theme.InputHeight, Margin = new Thickness(4), Width = 170, VerticalContentAlignment = VerticalAlignment.Center, SelectedDateFormat = DatePickerFormat.Short };
        d.SetBinding(DatePicker.SelectedDateProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        return d;
    }

    /// <summary>Read-only data grid with localized headers.</summary>
    public static DataGrid Table(string itemsPath, params (string HeaderKey, string Path, double Width)[] columns)
    {
        var g = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            FontSize = Theme.FontSmall,
            RowHeight = 40,
            ColumnHeaderHeight = 44,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = Theme.Border,
            BorderBrush = Theme.Border,
            Background = Theme.Surface,
            AlternatingRowBackground = Theme.Brush("#F8FAFC"),
            Margin = new Thickness(4),
        };
        g.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(itemsPath));
        foreach (var (header, path, width) in columns)
        {
            var h = new TextBlock { FontWeight = FontWeights.SemiBold };
            h.SetBinding(TextBlock.TextProperty, Loc.Bind(header));
            var col = new DataGridTextColumn
            {
                Header = h,
                Binding = new Binding(path),
                Width = width > 0 ? new DataGridLength(width, DataGridLengthUnitType.Star) : DataGridLength.Auto,
            };
            col.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap), new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center) } };
            g.Columns.Add(col);
        }
        return g;
    }

    public static Border Card(UIElement child, string? titleKey = null)
    {
        UIElement content = child;
        if (titleKey is not null)
        {
            var dock = new DockPanel();
            var title = Text(titleKey, Theme.FontLarge, true).Margin(4, 0, 4, 8);
            DockPanel.SetDock(title, Dock.Top);
            dock.Children.Add(title);
            dock.Children.Add(child);
            content = dock;
        }
        return new Border
        {
            Background = Theme.Surface,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(6),
            Child = content,
        };
    }

    public static Grid Layout(string columns, string rows = "*")
    {
        var g = new Grid();
        foreach (var c in columns.Split(',')) g.ColumnDefinitions.Add(new ColumnDefinition { Width = Length(c) });
        foreach (var r in rows.Split(',')) g.RowDefinitions.Add(new RowDefinition { Height = Length(r) });
        return g;
    }

    public static Grid Add(this Grid grid, params UIElement[] children)
    {
        foreach (var c in children) grid.Children.Add(c);
        return grid;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static WrapPanel Wrap(params UIElement[] children)
    {
        var p = new WrapPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>Two-column form: label key → input.</summary>
    public static Grid Form(params (string LabelKey, UIElement Input)[] rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.Children.Add(Text(rows[i].LabelKey, Theme.FontNormal, color: Theme.TextMuted).At(i, 0));
            g.Children.Add(rows[i].Input.At(i, 1));
        }
        return g;
    }

    public static Border Pill(string textPath, string brushPath, IValueConverter? brushConverter = null)
    {
        var text = Value(textPath, Theme.FontSmall, true, Theme.TextOnDark);
        var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Child = text };
        b.SetBinding(Border.BackgroundProperty, new Binding(brushPath) { Converter = brushConverter });
        return b;
    }

    private static GridLength Length(string s)
    {
        s = s.Trim();
        if (s == "auto" || s == "Auto") return GridLength.Auto;
        if (s.EndsWith('*')) return new GridLength(s.Length == 1 ? 1 : double.Parse(s[..^1], CultureInfo.InvariantCulture), GridUnitType.Star);
        return new GridLength(double.Parse(s, CultureInfo.InvariantCulture));
    }
}

/// <summary>Opens the Windows touch keyboard (TabTip) when an input gets focus on the touch panel.</summary>
public static class TouchKeyboard
{
    public static bool Enabled { get; set; } = true;

    public static void Attach(Control control)
    {
        control.GotFocus += (_, _) => Show();
        control.TouchDown += (_, _) => Show();
    }

    public static void Show()
    {
        if (!Enabled || !OperatingSystem.IsWindows()) return;
        try
        {
            var tabTip = Environment.ExpandEnvironmentVariables(@"%CommonProgramFiles%\microsoft shared\ink\TabTip.exe");
            if (System.IO.File.Exists(tabTip)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tabTip) { UseShellExecute = true });
        }
        catch
        {
            // keyboard is a convenience only
        }
    }
}
