using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Operation;

/// <summary>Operation screen: batch card on the left, Box and Leaflet camera panels on the right.</summary>
public sealed class OperationView : UserControl
{
    public OperationView(OperationViewModel vm)
    {
        DataContext = vm;
        var root = UiKit.Layout("520,*,*");
        root.Add(BatchCard().At(0, 0), CameraCard(nameof(OperationViewModel.BoxCamera)).At(0, 1), CameraCard(nameof(OperationViewModel.LeafletCamera)).At(0, 2));
        Content = root;
    }

    private static UIElement BatchCard()
    {
        var product = UiKit.TextBox(nameof(OperationViewModel.ProductName)).Bind(IsEnabledProperty, nameof(OperationViewModel.CanEditBatch));
        var batch = UiKit.TextBox(nameof(OperationViewModel.BatchNo)).Bind(IsEnabledProperty, nameof(OperationViewModel.CanEditBatch));
        var box = UiKit.Combo(nameof(OperationViewModel.BoxRecipes), nameof(OperationViewModel.BoxRecipe), nameof(RecipeOption.Display)).Bind(IsEnabledProperty, nameof(OperationViewModel.CanEditBatch));
        var leaflet = UiKit.Combo(nameof(OperationViewModel.LeafletRecipes), nameof(OperationViewModel.LeafletRecipe), nameof(RecipeOption.Display)).Bind(IsEnabledProperty, nameof(OperationViewModel.CanEditBatch));
        var form = UiKit.Form(("ui.productName", product), ("ui.batchNo", batch), ("ui.boxRecipe", box), ("ui.leafletRecipe", leaflet));

        var status = UiKit.Pill(nameof(OperationViewModel.StatusText), nameof(OperationViewModel.StatusBrush));
        status.HorizontalAlignment = HorizontalAlignment.Left;
        var info = UiKit.Column(
            UiKit.Row(UiKit.Text("ui.status", Theme.FontNormal, color: Theme.TextMuted), status).Margin(0, 10, 0, 4),
            UiKit.Row(UiKit.Text("col.start", Theme.FontNormal, color: Theme.TextMuted), UiKit.Value(nameof(OperationViewModel.StartText)).Margin(10, 0, 0, 0)),
            UiKit.Value(nameof(OperationViewModel.SetpointText), Theme.FontSmall, color: Theme.TextMuted).Margin(0, 4, 0, 0));

        var buttons = UiKit.Wrap(
            UiKit.Button("ui.createBatch", null!, Theme.Primary, 230).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(OperationViewModel.CreateCommand)),
            UiKit.Button("ui.start", null!, Theme.Success, 230).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(OperationViewModel.StartCommand)),
            UiKit.Button("ui.pause", null!, Theme.Warning, 230).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(OperationViewModel.PauseCommand)),
            UiKit.Button("ui.resume", null!, Theme.Success, 230).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(OperationViewModel.ResumeCommand)),
            UiKit.Button("ui.endBatch", null!, Theme.Danger, 468).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(OperationViewModel.EndCommand)));
        buttons.Margin = new Thickness(0, 12, 0, 12);

        var finished = UiKit.Column(UiKit.Text("col.finished", Theme.FontNormal, color: Theme.TextMuted), UiKit.Value(nameof(OperationViewModel.FinishedGoods), Theme.FontHuge, true, Theme.Primary));
        var process = UiKit.Layout("*,*", "auto,auto");
        process.Add(
            UiKit.Text("ui.speedActual", Theme.FontNormal, color: Theme.TextMuted).At(0, 0),
            UiKit.Text("ui.tempActual", Theme.FontNormal, color: Theme.TextMuted).At(0, 1),
            UiKit.Value(nameof(OperationViewModel.Speed), Theme.FontTitle, true).Bind(TextBlock.ForegroundProperty, nameof(OperationViewModel.SpeedBrush)).At(1, 0),
            UiKit.Value(nameof(OperationViewModel.Temperature), Theme.FontTitle, true).Bind(TextBlock.ForegroundProperty, nameof(OperationViewModel.TempBrush)).At(1, 1));
        process.Margin = new Thickness(0, 12, 0, 0);

        return UiKit.Card(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = UiKit.Column(form, info, buttons, finished, process) }, "ui.batch");
    }

    private static UIElement CameraCard(string path)
    {
        var header = UiKit.Layout("*,auto");
        var title = UiKit.Column(UiKit.Text("", Theme.FontLarge, true).Bind(TextBlock.TextProperty, nameof(CameraPanelViewModel.TitleKey), new FuncConverter(k => Loc.T((string)k!))),
            UiKit.Value(nameof(CameraPanelViewModel.Address), Theme.FontSmall, color: Theme.TextMuted));
        var dot = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Center };
        dot.SetBinding(Border.BackgroundProperty, new Binding(nameof(CameraPanelViewModel.Connected)) { Converter = FuncConverter.OnlineBrush });
        header.Add(title.At(0, 0), dot.At(0, 1));

        var image = new Image { Stretch = Stretch.Uniform, Height = 330, Margin = new Thickness(0, 8, 0, 8) };
        image.SetBinding(Image.SourceProperty, new Binding(nameof(CameraPanelViewModel.Image)));
        var imageFrame = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(6), Child = image };

        var result = UiKit.Layout("*,auto");
        result.Add(UiKit.Value(nameof(CameraPanelViewModel.LastCode), Theme.FontTitle, true).At(0, 0),
            UiKit.Pill(nameof(CameraPanelViewModel.LastOutcomeText), nameof(CameraPanelViewModel.LastOutcomeBrush)).At(0, 1));
        result.Margin = new Thickness(0, 4, 0, 8);

        var counters = UiKit.Layout("*,*,*,*", "auto,auto");
        counters.Add(
            UiKit.Text("col.total", Theme.FontSmall, color: Theme.TextMuted).At(0, 0),
            UiKit.Text("col.pass", Theme.FontSmall, color: Theme.TextMuted).At(0, 1),
            UiKit.Text("col.fail", Theme.FontSmall, color: Theme.TextMuted).At(0, 2),
            UiKit.Text("col.noRead", Theme.FontSmall, color: Theme.TextMuted).At(0, 3),
            UiKit.Value(nameof(CameraPanelViewModel.Scanned), Theme.FontTitle, true).At(1, 0),
            UiKit.Value(nameof(CameraPanelViewModel.Pass), Theme.FontTitle, true, Theme.Success).At(1, 1),
            UiKit.Value(nameof(CameraPanelViewModel.Fail), Theme.FontTitle, true, Theme.Danger).At(1, 2),
            UiKit.Value(nameof(CameraPanelViewModel.NoRead), Theme.FontTitle, true, Theme.Warning).At(1, 3));

        var connect = UiKit.Button("ui.connect", null!, Theme.Primary, 170).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(CameraPanelViewModel.ConnectCommand));
        ((TextBlock)connect.Content).SetBinding(TextBlock.TextProperty, new Binding(nameof(CameraPanelViewModel.ConnectKey)) { Converter = new FuncConverter(k => Loc.T((string)k!)) });
        var live = UiKit.Button("ui.liveStart", null!, Theme.Neutral, 150).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(CameraPanelViewModel.LiveCommand));
        ((TextBlock)live.Content).SetBinding(TextBlock.TextProperty, new Binding(nameof(CameraPanelViewModel.LiveKey)) { Converter = new FuncConverter(k => Loc.T((string)k!)) });
        var reset = UiKit.Button("ui.resetCounters", null!, Theme.Neutral, 170).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(CameraPanelViewModel.ResetCommand));

        var fails = new ListBox { FontSize = Theme.FontSmall, Height = 190, Margin = new Thickness(0, 4, 0, 0), BorderBrush = Theme.Border };
        fails.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(CameraPanelViewModel.FailList)));

        var body = UiKit.Column(header, imageFrame, result, counters, UiKit.Wrap(connect, live, reset), UiKit.Text("ui.failList", Theme.FontNormal, true).Margin(0, 8, 0, 0), fails);
        var card = UiKit.Card(body);
        card.SetBinding(DataContextProperty, new Binding(path));
        return card;
    }
}
