using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Pages.Operation;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Images;

public sealed record ImageRow(long Id, string Time, string BatchNo, string Camera, string Result, string Code, string? Path);

public sealed record OutcomeOption(InspectionOutcome? Outcome, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Image management: list with filters (date, code, status) and viewer (SYS-06).</summary>
public sealed class ImagesViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;
    private DateTime? _from = DateTime.Today;
    private DateTime? _to = DateTime.Today;
    private string _code = "";
    private OutcomeOption? _outcome;
    private ImageRow? _selected;
    private ImageSource? _image;

    public ImagesViewModel(AppHost host)
    {
        _host = host;
        SearchCommand = new RelayCommand(Search);
        host.Localizer.LanguageChanged += (_, _) => OnUi(OnShown);
    }

    public ObservableCollection<ImageRow> Rows { get; } = new();
    public ObservableCollection<OutcomeOption> Outcomes { get; } = new();
    public ICommand SearchCommand { get; }
    public DateTime? From { get => _from; set => Set(ref _from, value); }
    public DateTime? To { get => _to; set => Set(ref _to, value); }
    public string Code { get => _code; set => Set(ref _code, value); }
    public OutcomeOption? Outcome { get => _outcome; set => Set(ref _outcome, value); }
    public ImageSource? Image { get => _image; private set => Set(ref _image, value); }

    public ImageRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Image = value?.Path is { } p ? PngImage.FromPng(_host.Images.Load(p)) : null;
        }
    }

    public void OnShown()
    {
        Outcomes.Clear();
        Outcomes.Add(new OutcomeOption(null, Loc.T("ui.all")));
        foreach (var o in Enum.GetValues<InspectionOutcome>()) Outcomes.Add(new OutcomeOption(o, Loc.T("outcome." + o)));
        Outcome = Outcomes[0];
        Search();
    }

    private void Search()
    {
        var from = _host.Time.ToUtc((From ?? DateTime.Today).Date);
        var to = _host.Time.ToUtc((To ?? DateTime.Today).Date.AddDays(1));
        var batches = new Dictionary<long, string>();
        Rows.Clear();
        foreach (var r in _host.BatchesRepo.SearchResults(from, to, Code, Outcome?.Outcome, 2000))
        {
            if (!batches.TryGetValue(r.BatchId, out var no)) batches[r.BatchId] = no = _host.BatchesRepo.Find(r.BatchId)?.BatchNo ?? "";
            Rows.Add(new ImageRow(r.Id, _host.Time.DateTime(r.Utc), no, Loc.T("camera." + r.Camera), Loc.T("outcome." + r.Outcome), r.ReadString.Length == 0 ? "N/A" : r.ReadString, r.ImagePath));
        }
    }
}

public sealed class ImagesView : UserControl
{
    public ImagesView(ImagesViewModel vm)
    {
        DataContext = vm;
        var filter = UiKit.Wrap(UiKit.Text("col.from"), UiKit.Date(nameof(ImagesViewModel.From)), UiKit.Text("col.to"), UiKit.Date(nameof(ImagesViewModel.To)),
            UiKit.Text("col.barcode"), UiKit.TextBox(nameof(ImagesViewModel.Code), 160),
            UiKit.Text("col.result"), UiKit.Combo(nameof(ImagesViewModel.Outcomes), nameof(ImagesViewModel.Outcome), nameof(OutcomeOption.Display), 200),
            UiKit.Button("ui.filter", null!, Theme.Primary, 140).Bind(ButtonBase.CommandProperty, nameof(ImagesViewModel.SearchCommand)));
        var grid = UiKit.Table(nameof(ImagesViewModel.Rows), ("col.time", nameof(ImageRow.Time), 1.3), ("col.batchNo", nameof(ImageRow.BatchNo), 1),
            ("col.camera", nameof(ImageRow.Camera), 0.7), ("col.result", nameof(ImageRow.Result), 0.9), ("col.readString", nameof(ImageRow.Code), 1.2));
        grid.SetBinding(Selector.SelectedItemProperty, new System.Windows.Data.Binding(nameof(ImagesViewModel.Selected)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        var list = new DockPanel();
        DockPanel.SetDock(filter, Dock.Top);
        list.Children.Add(filter);
        list.Children.Add(grid);

        var image = new System.Windows.Controls.Image { Stretch = Stretch.Uniform };
        image.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding(nameof(ImagesViewModel.Image)));
        var frame = new Border { Background = Brushes.Black, Child = image };

        var root = UiKit.Layout("*,*");
        root.Add(UiKit.Card(list, "ui.nav.images").At(0, 0), UiKit.Card(frame, "ui.image").At(0, 1));
        Content = root;
    }
}
