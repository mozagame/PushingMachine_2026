using CheckBarcode.Domain;
using CheckBarcode.Infrastructure.Migration;
using CheckBarcode.Infrastructure.Sqlite;
using CheckBarcode.Tests.Framework;

namespace CheckBarcode.Tests;

public sealed class MigrationTests
{
    [Test("REC-01")]
    public async Task Imports_v1_recipes_through_the_audited_service()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        var v1 = Path.Combine(env.Dir, "MyDatabase.db");
        using (var db = new SqliteDatabase(v1))
        {
            db.Execute("CREATE TABLE [Recipe]([ID] INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,Name TEXT NULL,Barcode TEXT NULL,NhietDo TEXT NULL,TocDo TEXT NULL,Type INTEGER NULL)");
            db.Execute("INSERT INTO Recipe(Name, Barcode, NhietDo, TocDo, Type) VALUES('Partamol eff .', '512', '110', '55', 1)");
            db.Execute("INSERT INTO Recipe(Name, Barcode, NhietDo, TocDo, Type) VALUES('Partamol eff.', '767', '', '', 2)");
            db.Execute("INSERT INTO Recipe(Name, Barcode, NhietDo, TocDo, Type) VALUES('partamol eff .', '512', '110', '55', 1)");
        }
        env.LoginAdmin();
        var result = await new LegacyImporter(env.Host.Recipes).ImportRecipesAsync(v1);
        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Skipped.Count, "duplicate name skipped");
        var box = env.Host.Recipes.List(CameraRole.Box).Single();
        Assert.Equal(55.0, box.SpeedSetpoint);
        Assert.Equal(3.0, box.TempTolerance);
        Assert.True(env.Audit("RECIPE_CREATE").Count > 0, "import is audited");
    }

    [Test("REC-01")]
    public async Task Imports_the_real_v1_database_when_available()
    {
        var real = "/home/claude/cb/CheckBarcode/MyDatabase.db";
        if (!File.Exists(real)) return;
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        var copy = Path.Combine(env.Dir, "v1.db");
        File.Copy(real, copy);
        env.LoginAdmin();
        var result = await new LegacyImporter(env.Host.Recipes).ImportRecipesAsync(copy);
        Console.WriteLine($"    v1 import: {result.Imported} imported, {result.Skipped.Count} skipped: {string.Join(" | ", result.Skipped.Take(8))}");
        Assert.True(result.Imported > 50);
    }
}
