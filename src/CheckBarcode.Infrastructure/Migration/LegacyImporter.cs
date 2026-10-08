using System.Globalization;
using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Recipes;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure.Sqlite;

namespace CheckBarcode.Infrastructure.Migration;

public sealed record ImportResult(int Imported, IReadOnlyList<string> Skipped);

/// <summary>
/// Imports recipes from the v1 database (MyDatabase.db, table Recipe(ID, Name, Barcode, NhietDo, TocDo, Type)).
/// Every recipe is created through RecipeService, so each import is a normal audited RECIPE_CREATE.
/// Users are not migrated: v1 stored passwords in clear text.
/// </summary>
public sealed class LegacyImporter
{
    private readonly RecipeService _recipes;

    public LegacyImporter(RecipeService recipes) => _recipes = recipes;

    public async Task<ImportResult> ImportRecipesAsync(string v1DatabasePath, double tempTolerance = 3, double speedTolerance = 5)
    {
        if (!File.Exists(v1DatabasePath)) throw new FileNotFoundException("v1 database not found", v1DatabasePath);
        List<SqliteRow> rows;
        using (var db = new SqliteDatabase(v1DatabasePath, readOnly: true))
        {
            rows = db.Query("SELECT Name, Barcode, NhietDo, TocDo, Type FROM Recipe ORDER BY ID");
        }

        var imported = 0;
        var skipped = new List<string>();
        foreach (var row in rows)
        {
            var name = row.Str("Name").Trim();
            var type = row.Long("Type") == 1 ? CameraRole.Box : CameraRole.Leaflet;
            double? temp = Parse(row.Str("NhietDo"));
            double? speed = Parse(row.Str("TocDo"));
            var box = type == CameraRole.Box;
            var input = new RecipeInput(name, type, row.Str("Barcode").Trim(), BarcodeFormat.Pharmacode,
                box ? speed : null, box ? speedTolerance : null, box ? temp : null, box ? tempTolerance : null);
            var outcome = await _recipes.CreateAsync(input);
            if (outcome.Status == ActionStatus.Success) imported++;
            else skipped.Add($"{name} [{type}]: {outcome.MessageKey}");
        }
        return new ImportResult(imported, skipped);
    }

    private static double? Parse(string s) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
