using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Xva;

/// <summary>Who decided a field's value (docs/04): the source, an automatic rule, or the user.</summary>
public enum Ownership { Source, Automatic, User }

/// <summary>A trade as the workspace shows it and Price would submit it: source, rules and overrides applied.</summary>
public sealed record EffectiveTrade(
    string TradeId,
    bool IsNew,
    string Product,
    string Currency,
    decimal Notional,
    string Maturity,
    decimal? FixedRate,
    string? Schedule,
    decimal? LastFixing,
    string Csa,
    Ownership CsaOwner,
    string Curve,
    Ownership CurveOwner,
    bool Before,
    bool After,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Held);

/// <summary>The counterparty's credit inputs. An empty CVA recovery stays empty; analytics uses the CDS recovery.</summary>
public sealed record EffectiveCredit(decimal? CvaRecovery, Ownership CvaRecoveryOwner, decimal CdsRecovery);

/// <summary>XVA's edit kinds, scopes and conflict groups (ADR-0003 §7), registered with the kernel.</summary>
public sealed class XvaEditRules : IEditRules
{
    public const string App = "xva";

    public string KindOf(EditTarget target) => (Entity.Split(target.Entity).Kind, target.Field) switch
    {
        (Entity.Trade, "before" or "after") => "xva.membership",
        (Entity.Trade, "csa" or "curve") => "xva.assignment",
        (Entity.Trade, _) => "xva.trade",
        (Entity.Csa, _) => "xva.csa",
        (Entity.Credit, _) => "xva.credit",
        _ => throw new ArgumentException($"No XVA edit kind for {target.Entity}.{target.Field}."),
    };

    // A fixed rate means something else once its schedule changes, so the two are compared together.
    public IReadOnlyList<string> ConflictGroup(EditTarget target) =>
        target.Field is "fixedRate" or "schedule" ? ["fixedRate", "schedule"] : [target.Field];

    /// <summary>
    /// The scope of an edit, decided here and never taken from the client: a new trade's own content belongs to
    /// that trade and survives a counterparty change; everything else belongs to the counterparty registration.
    /// </summary>
    public static EditTarget Target(XvaSourceVersion source, string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        var tradeContent = kind == Entity.Trade && field is not ("before" or "after" or "csa" or "curve");
        var isNew = kind == Entity.Trade && source.Trade(id)?.IsNew == true;
        return new EditTarget(tradeContent && isNew ? $"new:{id}" : $"cp:{source.Registration.Key}", entity, field);
    }
}

public static class XvaMaterialiser
{
    public static IReadOnlyList<string> EditableFields { get; } = ["notional", "fixedRate", "schedule", "csa", "curve", "before", "after"];

    public static IReadOnlyList<EffectiveTrade> Trades(WorkspaceState state)
    {
        var source = (XvaSourceVersion)state.Source;
        var overrides = state.Overrides.Values.ToDictionary(o => (o.Target.Entity, o.Target.Field), o => o.Value);
        var held = state.Conflicts.Keys.Select(t => (t, "conflict")).Concat(state.Lost.Keys.Select(t => (t, "lost")))
            .ToLookup(x => x.t.Entity, x => $"{x.t.Field}: {x.Item2}");
        return [.. source.Existing.Concat(source.New).Select(t => Trade(source, t, overrides, held))];
    }

    /// <summary>The entities Price would read: trades in Before or After, the CSAs they use, and the credit inputs.</summary>
    public static Func<string, bool> PricedEntities(WorkspaceState state)
    {
        var source = (XvaSourceVersion)state.Source;
        var trades = Trades(state).Where(t => t.Before || t.After).ToList();
        var entities = trades.Select(t => Entity.Of(Entity.Trade, t.TradeId))
            .Concat(trades.Select(t => Entity.Of(Entity.Csa, t.Csa)))
            .Append(Entity.Of(Entity.Credit, source.Registration.Key))
            .ToHashSet();
        return entities.Contains;
    }

    public static EffectiveCredit Credit(WorkspaceState state)
    {
        var source = (XvaSourceVersion)state.Source;
        var entity = Entity.Of(Entity.Credit, source.Registration.Key);
        var o = state.Overrides.Values.SingleOrDefault(x => x.Target.Entity == entity && x.Target.Field == "cvaRecovery");
        return o is null
            ? new(source.CvaRecovery, Ownership.Source, source.CdsRecovery)
            : new(o.Value?.GetValue<decimal>(), Ownership.User, source.CdsRecovery);
    }

    static EffectiveTrade Trade(XvaSourceVersion source, SourceTrade t, Dictionary<(string, string), JsonNode?> overrides, ILookup<string, string> held)
    {
        var entity = Entity.Of(Entity.Trade, t.TradeId);
        bool Has(string field, out JsonNode? value) => overrides.TryGetValue((entity, field), out value);
        var agreement = source.Agreement;
        var issues = new List<string>();

        string csa;
        Ownership csaOwner;
        if (Has("csa", out var csaValue)) (csa, csaOwner) = (csaValue!.GetValue<string>(), Ownership.User);
        else if (t.IsNew) (csa, csaOwner) = (agreement.MainCsaId ?? agreement.NoCsa, Ownership.Automatic);
        else (csa, csaOwner) = (t.Csa ?? throw new InvalidOperationException($"Existing trade {t.TradeId} has no CSA."), Ownership.Source);

        var eligible = csa == agreement.NoCsa || agreement.Csas.Any(c => c.CsaId == csa);
        if (!eligible) issues.Add($"CSA {csa} is not eligible under {agreement.MaId}.");

        string curve;
        Ownership curveOwner;
        if (Has("curve", out var curveValue)) (curve, curveOwner) = (curveValue!.GetValue<string>(), Ownership.User);
        else if (csa == agreement.NoCsa) (curve, curveOwner) = (XvaCatalog.Profile.UncollateralisedCurve, Ownership.Automatic);
        else if (source.CsaOf(csa) is { } c)
            (curve, curveOwner) = (overrides.TryGetValue((Entity.Of(Entity.Csa, c.CsaId), "curve"), out var csaCurve) ? csaCurve!.GetValue<string>() : c.Curve, Ownership.Automatic);
        else (curve, curveOwner) = ("", Ownership.Automatic);
        if (!XvaCatalog.Curves.Contains(curve)) issues.Add(curve == "" ? "No discount curve: the CSA is not eligible." : $"Curve {curve} is not available.");

        return new(
            t.TradeId, t.IsNew, t.Product, t.Currency,
            Has("notional", out var n) ? n!.GetValue<decimal>() : t.Notional,
            t.Maturity.ToString("yyyy-MM-dd"),
            Has("fixedRate", out var r) ? r?.GetValue<decimal>() : t.FixedRate,
            Has("schedule", out var s) ? s?.GetValue<string>() : t.Schedule,
            t.LastFixing,
            csa, csaOwner, curve, curveOwner,
            Has("before", out var b) ? b!.GetValue<bool>() : !t.IsNew,
            Has("after", out var a) ? a!.GetValue<bool>() : true,
            issues,
            [.. held[entity]]);
    }
}
