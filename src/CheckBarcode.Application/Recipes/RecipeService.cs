using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Recipes;

public static class RecipeActions
{
    public const string Create = "RECIPE_CREATE";
    public const string Edit = "RECIPE_EDIT";
    public const string Delete = "RECIPE_DELETE";
}

/// <summary>Editable recipe fields (REC-02).</summary>
public sealed record RecipeInput(string Name, CameraRole Type, string Barcode, BarcodeFormat BarcodeFormat,
    double? SpeedSetpoint, double? SpeedTolerance, double? TempSetpoint, double? TempTolerance);

/// <summary>Recipe management with versioning and soft delete (REC-01..06).</summary>
public sealed class RecipeService
{
    private readonly IRecipeRepository _recipes;
    private readonly IBatchRepository _batches;
    private readonly ActionDispatcher _dispatcher;
    private readonly IClock _clock;

    public RecipeService(IRecipeRepository recipes, IBatchRepository batches, ActionDispatcher dispatcher, IClock clock)
    {
        _recipes = recipes;
        _batches = batches;
        _dispatcher = dispatcher;
        _clock = clock;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<Recipe> List(CameraRole? type = null) => _recipes.ListActive(type);
    public Recipe? Find(long id) => _recipes.Find(id);

    public Task<ActionOutcome> CreateAsync(RecipeInput input) =>
        Run(_dispatcher.ExecuteAsync(RecipeActions.Create, s =>
        {
            Validate(input);
            if (_recipes.FindActiveByName(input.Name.Trim(), input.Type) is not null) throw new DomainException("msg.recipeNameExists", input.Name);
            var recipe = new Recipe { CreatedUtc = _clock.UtcNow, UpdatedUtc = _clock.UtcNow, Version = 1 };
            Apply(recipe, input);
            recipe.Id = _recipes.Insert(recipe);
            s.Target("Recipe", $"{recipe.Name} (#{recipe.Id})");
            s.Args["name"] = recipe.Name;
            foreach (var (field, value) in Fields(recipe)) s.Change(field, null, value);
        }));

    /// <summary>Edit requires reason + e-signature (actions.json). Increments the version.</summary>
    public Task<ActionOutcome> EditAsync(long id, RecipeInput input) =>
        Run(_dispatcher.ExecuteAsync(RecipeActions.Edit, s =>
        {
            var recipe = _recipes.Find(id);
            if (recipe is null || recipe.Status != RecordStatus.Active) throw new DomainException("msg.recipeNotFound");
            EnsureNotInUse(recipe);
            Validate(input);
            var other = _recipes.FindActiveByName(input.Name.Trim(), input.Type);
            if (other is not null && other.Id != id) throw new DomainException("msg.recipeNameExists", input.Name);
            if (input.Type != recipe.Type) throw new DomainException("msg.recipeTypeFixed");

            var before = Fields(recipe).ToDictionary(f => f.Field, f => f.Value);
            Apply(recipe, input);
            recipe.Version++;
            recipe.UpdatedUtc = _clock.UtcNow;
            _recipes.Update(recipe);

            s.Target("Recipe", $"{recipe.Name} (#{recipe.Id})");
            s.Args["name"] = recipe.Name;
            foreach (var (field, value) in Fields(recipe)) s.Change(field, before[field], value);
            s.Change("Version", recipe.Version - 1, recipe.Version);
        }));

    /// <summary>Soft delete; the name can be reused afterwards (REC-03).</summary>
    public Task<ActionOutcome> DeleteAsync(long id) =>
        Run(_dispatcher.ExecuteAsync(RecipeActions.Delete, s =>
        {
            var recipe = _recipes.Find(id);
            if (recipe is null || recipe.Status != RecordStatus.Active) throw new DomainException("msg.recipeNotFound");
            EnsureNotInUse(recipe);
            recipe.Status = RecordStatus.Deleted;
            recipe.UpdatedUtc = _clock.UtcNow;
            _recipes.Update(recipe);
            s.Target("Recipe", $"{recipe.Name} (#{recipe.Id})");
            s.Args["name"] = recipe.Name;
            s.Change("Status", RecordStatus.Active, RecordStatus.Deleted);
        }));

    private async Task<ActionOutcome> Run(Task<ActionOutcome> action)
    {
        var outcome = await action;
        if (outcome.Ok) Changed?.Invoke(this, EventArgs.Empty);
        return outcome;
    }

    private void EnsureNotInUse(Recipe recipe)
    {
        var open = _batches.FindOpen();
        if (open is not null && (open.BoxRecipeId == recipe.Id || open.LeafletRecipeId == recipe.Id))
            throw new DomainException("msg.recipeInUse", open.BatchNo);
    }

    private static void Validate(RecipeInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new DomainException("msg.recipeNameRequired");
        if (string.IsNullOrWhiteSpace(input.Barcode)) throw new DomainException("msg.recipeBarcodeRequired");
        if (input.Type == CameraRole.Box)
        {
            if (input.SpeedSetpoint is null or < 0 || input.TempSetpoint is null or < 0) throw new DomainException("msg.recipeSetpointsRequired");
            if (input.SpeedTolerance is < 0 || input.TempTolerance is < 0) throw new DomainException("msg.recipeToleranceInvalid");
        }
    }

    private static void Apply(Recipe r, RecipeInput i)
    {
        r.Name = i.Name.Trim();
        r.Type = i.Type;
        r.Barcode = i.Barcode.Trim();
        r.BarcodeFormat = i.BarcodeFormat;
        var box = i.Type == CameraRole.Box;
        r.SpeedSetpoint = box ? i.SpeedSetpoint : null;
        r.SpeedTolerance = box ? i.SpeedTolerance ?? 0 : null;
        r.TempSetpoint = box ? i.TempSetpoint : null;
        r.TempTolerance = box ? i.TempTolerance ?? 0 : null;
    }

    private static IEnumerable<(string Field, string Value)> Fields(Recipe r)
    {
        yield return ("Name", r.Name);
        yield return ("Type", r.Type.ToString());
        yield return ("Barcode", r.Barcode);
        yield return ("BarcodeFormat", r.BarcodeFormat.ToString());
        yield return ("SpeedSetpoint", ActionScope.Format(r.SpeedSetpoint));
        yield return ("SpeedTolerance", ActionScope.Format(r.SpeedTolerance));
        yield return ("TempSetpoint", ActionScope.Format(r.TempSetpoint));
        yield return ("TempTolerance", ActionScope.Format(r.TempTolerance));
    }
}

/// <summary>Compares the read string with the expected code. Replaceable for GS1 / serialization later.</summary>
public interface ICodeMatcher
{
    InspectionOutcome Evaluate(Recipe recipe, string readString);
}

public sealed class ExactCodeMatcher : ICodeMatcher
{
    public InspectionOutcome Evaluate(Recipe recipe, string readString)
    {
        if (string.IsNullOrEmpty(readString)) return InspectionOutcome.NoRead;
        return string.Equals(readString.Trim(), recipe.Barcode.Trim(), StringComparison.Ordinal) ? InspectionOutcome.Pass : InspectionOutcome.Fail;
    }
}
