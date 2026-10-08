using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CheckBarcode.Application.Reports;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Reports;

/// <summary>Renders the same <see cref="ReportDocument"/> as the PDF writer into a WPF FlowDocument for preview and printing (RPT-06).</summary>
public static class FlowReportRenderer
{
    public static FlowDocument Render(ReportDocument report)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Arial"),
            FontSize = 12,
            PagePadding = new Thickness(40),
            ColumnWidth = double.PositiveInfinity,
            Background = Brushes.White,
        };
        doc.Blocks.Add(new Paragraph(new Bold(new Run(report.Title))) { FontSize = 20, Margin = new Thickness(0) });
        if (report.Subtitle.Length > 0) doc.Blocks.Add(new Paragraph(new Run(report.Subtitle)) { Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 8) });
        if (report.Header.Count > 0) doc.Blocks.Add(Fields(report.Header));

        foreach (var section in report.Sections)
        {
            if (section.Heading is { } h) doc.Blocks.Add(new Paragraph(new Bold(new Run(h))) { FontSize = 15, Margin = new Thickness(0, 12, 0, 4) });
            if (section.Fields.Count > 0) doc.Blocks.Add(Fields(section.Fields));
            if (section.Text is { } t) doc.Blocks.Add(new Paragraph(new Run(t)) { Foreground = Brushes.DimGray });
            if (section.Table is { } table) doc.Blocks.Add(Table(table));
        }
        doc.Blocks.Add(new Paragraph(new Run(report.Footer)) { FontSize = 10, Foreground = Brushes.DimGray, Margin = new Thickness(0, 16, 0, 0) });
        return doc;
    }

    private static Table Fields(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 6) };
        t.Columns.Add(new TableColumn { Width = new GridLength(190) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        foreach (var (k, v) in fields)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Bold(new Run(k + ":"))) { Margin = new Thickness(0, 1, 0, 1) }));
            row.Cells.Add(new TableCell(new Paragraph(new Run(v)) { Margin = new Thickness(0, 1, 0, 1) }));
            group.Rows.Add(row);
        }
        t.RowGroups.Add(group);
        return t;
    }

    private static Table Table(ReportTable source)
    {
        var t = new Table { CellSpacing = 0, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), FontSize = 11 };
        foreach (var c in source.Columns) t.Columns.Add(new TableColumn { Width = new GridLength(c.Weight, GridUnitType.Star) });
        var header = new TableRowGroup { Background = new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEA)) };
        var hr = new TableRow();
        foreach (var c in source.Columns) hr.Cells.Add(Cell(c.Header, true, TextAlignment.Left));
        header.Rows.Add(hr);
        t.RowGroups.Add(header);
        var body = new TableRowGroup();
        foreach (var r in source.Rows)
        {
            var row = new TableRow();
            for (var i = 0; i < source.Columns.Count; i++)
            {
                var align = source.Columns[i].Align switch { ColumnAlign.Right => TextAlignment.Right, ColumnAlign.Center => TextAlignment.Center, _ => TextAlignment.Left };
                row.Cells.Add(Cell(i < r.Length ? r[i] : "", false, align));
            }
            body.Rows.Add(row);
        }
        t.RowGroups.Add(body);
        return t;
    }

    private static TableCell Cell(string text, bool bold, TextAlignment align) =>
        new(new Paragraph(bold ? new Bold(new Run(text)) : new Run(text)) { Margin = new Thickness(3, 2, 3, 2), TextAlignment = align })
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 0.5, 0.5),
        };

    /// <summary>Prints with the Windows print dialog, inside the application (no PDF reader, no desktop).</summary>
    public static bool Print(ReportDocument report)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return false;
        var doc = Render(report);
        if (report.Landscape) dialog.PrintTicket.PageOrientation = System.Printing.PageOrientation.Landscape;
        doc.PageWidth = dialog.PrintableAreaWidth;
        doc.PageHeight = dialog.PrintableAreaHeight;
        doc.ColumnWidth = dialog.PrintableAreaWidth;
        dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, report.Title);
        return true;
    }
}
