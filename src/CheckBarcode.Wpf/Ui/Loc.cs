using System.ComponentModel;
using System.Windows.Data;
using CheckBarcode.Application.Localization;

namespace CheckBarcode.Wpf.Ui;

/// <summary>
/// Binding source for localized text: <c>{Binding [ui.login], Source=Loc.Instance}</c>.
/// Every bound text refreshes when the language changes (SYS-01).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private ILocalizer _localizer = new Localizer();

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public ILocalizer Localizer => _localizer;

    public void Attach(ILocalizer localizer)
    {
        _localizer = localizer;
        localizer.LanguageChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }

    public string this[string key] => _localizer.T(key);

    public static string T(string key, params object[] args) => Instance._localizer.T(key, args);

    public static Binding Bind(string key) => new($"[{key}]") { Source = Instance, Mode = BindingMode.OneWay };
}
