using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Localization;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Actions;

/// <summary>Credentials typed into the e-signature dialog (21 CFR 11.200(a)).</summary>
public sealed record SignatureInput(string Username, string Password);

/// <summary>UI port used by the dispatcher to ask for a reason or a signature.</summary>
public interface IActionPrompt
{
    /// <summary>Returns the reason, or null when the user cancels.</summary>
    Task<string?> AskReasonAsync(ActionDefinition action);
    /// <summary>Returns credentials, or null when the user cancels.</summary>
    Task<SignatureInput?> AskSignatureAsync(ActionDefinition action, string meaning);
}

/// <summary>Collects what a handler changed; the dispatcher turns it into audit records.</summary>
public sealed class ActionScope
{
    internal readonly List<(string ObjectType, string ObjectId, string Field, string? Old, string? New)> Changes = new();

    public ActionScope(ActionDefinition definition, string reason)
    {
        Definition = definition;
        Reason = reason;
    }

    public ActionDefinition Definition { get; }
    public string Reason { get; }
    public string ObjectType { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public long? BatchId { get; set; }
    public Dictionary<string, string> Args { get; } = new();

    public void Target(string objectType, string objectId)
    {
        ObjectType = objectType;
        ObjectId = objectId;
    }

    /// <summary>Records a field change (old → new). Unchanged values are ignored.</summary>
    public void Change(string field, object? oldValue, object? newValue)
    {
        var o = Format(oldValue);
        var n = Format(newValue);
        if (o == n) return;
        Changes.Add((ObjectType, ObjectId, field, o, n));
    }

    public static string Format(object? value) => value switch
    {
        null => "",
        double d => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O"),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}

public enum ActionStatus
{
    Success,
    Denied,
    Cancelled,
    SignatureFailed,
    Failed,
}

public sealed record ActionOutcome(ActionStatus Status, string MessageKey = "", object[]? MessageArgs = null, Exception? Error = null)
{
    public bool Ok => Status == ActionStatus.Success;
    public static readonly ActionOutcome Success = new(ActionStatus.Success);
}

/// <summary>
/// Single entry point for every user action (AUD-04):
/// permission → reason → e-signature → handler → audit (also when denied or failed).
/// </summary>
public sealed class ActionDispatcher
{
    private readonly IReadOnlyDictionary<string, ActionDefinition> _actions;
    private readonly PermissionService _permissions;
    private readonly SessionService _session;
    private readonly AuthService _auth;
    private readonly AuditTrail _audit;
    private readonly ILocalizer _loc;

    public ActionDispatcher(IEnumerable<ActionDefinition> actions, PermissionService permissions, SessionService session,
        AuthService auth, AuditTrail audit, ILocalizer loc)
    {
        _actions = actions.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
        _permissions = permissions;
        _session = session;
        _auth = auth;
        _audit = audit;
        _loc = loc;
    }

    public IActionPrompt? Prompt { get; set; }

    public ActionDefinition Definition(string code) =>
        _actions.TryGetValue(code, out var def) ? def : throw new InvalidOperationException($"Action '{code}' is not defined in actions.json");

    public bool IsDefined(string code) => _actions.ContainsKey(code);

    public IEnumerable<ActionDefinition> All => _actions.Values;

    /// <summary>True when the current session may run the action (used to enable buttons).</summary>
    public bool CanExecute(string code)
    {
        if (!_actions.TryGetValue(code, out var def)) return false;
        if (def.Permission == "*") return true;
        return _permissions.Has(def.Permission);
    }

    public async Task<ActionOutcome> ExecuteAsync(string code, Func<ActionScope, Task> handler, string? presetReason = null)
    {
        var def = Definition(code);
        _session.Touch();

        if (!CanExecute(code))
        {
            _audit.Write(AuditCodes.ActionDenied, "Action", code, args: new Dictionary<string, string> { ["action"] = code });
            return new ActionOutcome(ActionStatus.Denied, _session.IsLoggedIn ? "msg.permissionDenied" : "msg.loginRequired");
        }

        var reason = presetReason ?? "";
        if (def.RequireReason && string.IsNullOrWhiteSpace(reason))
        {
            if (Prompt is null) throw new InvalidOperationException("No prompt available for reason");
            var r = await Prompt.AskReasonAsync(def);
            if (string.IsNullOrWhiteSpace(r)) return new ActionOutcome(ActionStatus.Cancelled);
            reason = r.Trim();
        }

        string? signer = null;
        string meaning = def.SignatureMeaning?.Get(_loc.Language) ?? def.Text.Get(_loc.Language);
        if (def.RequireSignature)
        {
            if (Prompt is null) throw new InvalidOperationException("No prompt available for signature");
            var sig = await Prompt.AskSignatureAsync(def, meaning);
            if (sig is null) return new ActionOutcome(ActionStatus.Cancelled);
            var check = _auth.VerifySignature(sig.Username, sig.Password);
            if (!check.Ok)
            {
                _audit.Write(AuditCodes.SignatureFailed, "Action", code, reason: reason,
                    args: new Dictionary<string, string> { ["action"] = code, ["signer"] = sig.Username });
                return new ActionOutcome(ActionStatus.SignatureFailed, check.MessageKey);
            }
            signer = sig.Username;
        }

        var scope = new ActionScope(def, reason);
        try
        {
            await handler(scope);
        }
        catch (DomainException ex)
        {
            WriteFailure(code, scope, reason, ex.Code);
            return new ActionOutcome(ActionStatus.Failed, ex.Code, ex.Args, ex);
        }
        catch (Exception ex)
        {
            WriteFailure(code, scope, reason, ex.GetType().Name + ": " + ex.Message);
            return new ActionOutcome(ActionStatus.Failed, "msg.unexpectedError", new object[] { ex.Message }, ex);
        }

        var records = new List<Domain.AuditRecord>();
        if (scope.Changes.Count == 0)
        {
            records.Add(_audit.Write(code, scope.ObjectType, scope.ObjectId, reason: reason, args: scope.Args, batchId: scope.BatchId));
        }
        else
        {
            foreach (var c in scope.Changes)
                records.Add(_audit.Write(code, c.ObjectType, c.ObjectId, c.Field, c.Old, c.New, reason, scope.Args, batchId: scope.BatchId));
        }
        if (signer is not null)
        {
            foreach (var rec in records) _audit.Sign(rec, signer, meaning);
        }
        return ActionOutcome.Success;
    }

    /// <summary>Convenience overload for synchronous handlers.</summary>
    public Task<ActionOutcome> ExecuteAsync(string code, Action<ActionScope> handler, string? presetReason = null) =>
        ExecuteAsync(code, s => { handler(s); return Task.CompletedTask; }, presetReason);

    private void WriteFailure(string code, ActionScope scope, string reason, string error)
    {
        var args = new Dictionary<string, string>(scope.Args) { ["action"] = code, ["error"] = error };
        _audit.Write(AuditCodes.ActionFailed, scope.ObjectType, scope.ObjectId, reason: reason, args: args, batchId: scope.BatchId);
    }
}
