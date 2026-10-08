using System.Windows;
using System.Windows.Media;

namespace CheckBarcode.Wpf.Ui;

/// <summary>Colours and sizes for a 1920×1080 touch panel (min. touch target 48 px).</summary>
public static class Theme
{
    public static readonly Brush Background = Brush("#F3F5F8");
    public static readonly Brush Surface = Brush("#FFFFFF");
    public static readonly Brush TopBar = Brush("#1F2A44");
    public static readonly Brush Nav = Brush("#26334F");
    public static readonly Brush NavSelected = Brush("#3B82F6");
    public static readonly Brush TextOnDark = Brush("#FFFFFF");
    public static readonly Brush Text = Brush("#1F2937");
    public static readonly Brush TextMuted = Brush("#6B7280");
    public static readonly Brush Border = Brush("#D6DBE3");
    public static readonly Brush Primary = Brush("#2563EB");
    public static readonly Brush Success = Brush("#16A34A");
    public static readonly Brush Warning = Brush("#D97706");
    public static readonly Brush Danger = Brush("#DC2626");
    public static readonly Brush Neutral = Brush("#6B7280");
    public static readonly Brush AlarmBanner = Brush("#FEE2E2");
    public static readonly Brush Disabled = Brush("#9CA3AF");

    public const double FontNormal = 18;
    public const double FontSmall = 15;
    public const double FontLarge = 22;
    public const double FontTitle = 26;
    public const double FontHuge = 44;
    public const double ButtonHeight = 56;
    public const double InputHeight = 48;

    public static readonly FontFamily Font = new("Segoe UI");

    public static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Thickness Pad(double all) => new(all);
}
