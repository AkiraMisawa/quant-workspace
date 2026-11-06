using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Products;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Rates;

// ADR-0008: the IRS and the CCS as descriptions the kernel reads, and the rules a description cannot hold: their
// defaults, their mapping onto a line's flat fields (C40), and the economic check, which stays the line resolver's.

public static class RatesProducts
{
    public const string Irs = "rates.irs";
    public const string Ccs = "rates.ccs";
    public const string Version = "1.0.0";

    static readonly IReadOnlyDictionary<string, string> Directions = new Dictionary<string, string>
    {
        ["pay"] = "pay", ["p"] = "pay", ["payer"] = "pay", ["receive"] = "receive", ["rec"] = "receive", ["r"] = "receive", ["receiver"] = "receive",
    };

    static IReadOnlyDictionary<string, string> Currencies(ConventionRegistry conventions) =>
        conventions.Profiles.Select(p => p.Currency).Distinct(StringComparer.Ordinal).ToDictionary(c => c.ToLowerInvariant(), c => c);

    static IReadOnlyDictionary<string, string> Indices(ConventionRegistry conventions) =>
        conventions.Profiles.Select(p => p.Index).Distinct(StringComparer.Ordinal).ToDictionary(i => i.ToLowerInvariant(), i => i);

    /// <summary>An interest rate swap: a fixed leg 1 against a floating leg 2 on one currency, index and notional (C40).</summary>
    public static ProductDescriptor IrsDescriptor(ConventionRegistry conventions) => new(Irs, Version, RatesEditRules.App, RatesEditRules.LinesScope,
        "irs", "Interest rate swap",
        [
            new("end", "End: a tenor from the start, or a date", FieldForm.PeriodOrDate, Required: true, Unnamed: true),
            new("currency", "Currency", FieldForm.Choice, Required: true, Unnamed: true, Choices: Currencies(conventions)),
            new("index", "Floating index", FieldForm.Choice, Required: true, Unnamed: true, Choices: Indices(conventions)),
            new("notional", "Notional", FieldForm.Amount, Required: true, Unnamed: true),
            new("direction", "Pay or receive the fixed leg", FieldForm.Choice, Required: true, Unnamed: true, Choices: Directions),
            new("rate", "Fixed rate (empty: solved at par)", FieldForm.Percent, Unnamed: true),
            new("spread", "Floating spread", FieldForm.Percent),
            new("fixedFrequency", "Fixed leg payment frequency", FieldForm.Period, Required: true, Aliases: ["fixed"]),
            new("floatFrequency", "Floating leg payment frequency", FieldForm.Period, Required: true, Aliases: ["float"]),
            new("start", "Start: a tenor from spot, or a date (empty: spot)", FieldForm.PeriodOrDate),
            new("risk", "Risk", FieldForm.Flag),
        ]);

    /// <summary>A non-resetting floating/floating cross-currency swap with notional exchanges (C42).</summary>
    public static ProductDescriptor CcsDescriptor(ConventionRegistry conventions) => new(Ccs, Version, RatesEditRules.App, RatesEditRules.LinesScope,
        "ccs", "Cross-currency swap",
        [
            new("end", "End: a tenor from the start, or a date", FieldForm.PeriodOrDate, Required: true, Unnamed: true),
            new("currency1", "Leg 1 currency", FieldForm.Choice, Required: true, Unnamed: true, Sequence: "currency", Choices: Currencies(conventions), Aliases: ["ccy1"]),
            new("currency2", "Leg 2 currency", FieldForm.Choice, Required: true, Unnamed: true, Sequence: "currency", Choices: Currencies(conventions), Aliases: ["ccy2"]),
            new("notional1", "Leg 1 notional", FieldForm.Amount, Required: true, Unnamed: true, Sequence: "notional"),
            new("notional2", "Leg 2 notional", FieldForm.Amount, Required: true, Unnamed: true, Sequence: "notional"),
            new("direction", "Pay or receive leg 1", FieldForm.Choice, Required: true, Unnamed: true, Choices: Directions),
            new("index1", "Leg 1 index", FieldForm.Choice, Required: true, Choices: Indices(conventions)),
            new("index2", "Leg 2 index", FieldForm.Choice, Required: true, Choices: Indices(conventions)),
            new("spread1", "Leg 1 spread (empty: solved)", FieldForm.Percent),
            new("spread2", "Leg 2 spread (empty: solved)", FieldForm.Percent),
            new("frequency1", "Leg 1 payment frequency", FieldForm.Period, Required: true, Aliases: ["freq1"]),
            new("frequency2", "Leg 2 payment frequency", FieldForm.Period, Required: true, Aliases: ["freq2"]),
            new("start", "Start: a tenor from spot, or a date (empty: spot)", FieldForm.PeriodOrDate),
            new("risk", "Risk", FieldForm.Flag),
        ]);
}

/// <summary>The Rates module's catalog (ADR-0008 §7): the products it describes, from the configured conventions.</summary>
public sealed class RatesProductCatalog(ConventionRegistry conventions) : IProductCatalog
{
    public IEnumerable<ProductDescriptor> Products => [RatesProducts.IrsDescriptor(conventions), RatesProducts.CcsDescriptor(conventions)];
}

/// <summary>What the IRS and the CCS share: the workspace's defaults, a profile's templates, and creating the line.</summary>
public abstract class RatesProductRules(ConventionRegistry conventions, Func<RatesWorkspaces> workspaces) : IProductRules
{
    public abstract string ProductId { get; }
    protected ConventionRegistry Conventions => conventions;

    public abstract IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given);

    protected abstract (JsonObject Terms, IReadOnlyDictionary<string, string> Sources) Line(IReadOnlyDictionary<string, JsonNode?> terms, IReadOnlyDictionary<string, string> sources);

    public (WorkspaceState State, string Entity) Create(string workspaceId, long? revision, string user, IReadOnlyDictionary<string, JsonNode?> terms,
        IReadOnlyDictionary<string, string> sources, RunOrigin origin)
    {
        var (line, lineSources) = Line(terms, sources);
        try
        {
            return workspaces().CreateLine(workspaceId, user, line, origin, revision, lineSources);
        }
        catch (LineTermsRefused refused)
        {
            // The economic check is the line resolver's, as for a line typed in the grid (ADR-0008 §7).
            throw new ProductTermsRefused([.. refused.Issues.Select(i => new ProductIssue("InvalidTerms", null, i))]);
        }
    }

    protected const string Workspace = "workspace";
    protected static string ProfileSource(ConventionProfile p) => $"profile:{p.Id}@{p.Version}";

    /// <summary>The one profile of a currency, when it has exactly one: what an index left out may come from.</summary>
    protected ConventionProfile? OnlyProfileOf(string? currency) =>
        currency is null ? null : conventions.Profiles.Where(p => p.Currency == currency).ToList() is [var only] ? only : null;

    protected ConventionProfile? ProfileOf(string? currency, string? index) =>
        currency is null || index is null ? null : conventions.Find(currency, index);

    protected static string? Text(IReadOnlyDictionary<string, JsonNode?> values, string name) =>
        values.GetValueOrDefault(name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Copies a reading's field onto line fields, and its source with it.</summary>
    protected static void Put(JsonObject line, Dictionary<string, string> lineSources, IReadOnlyDictionary<string, JsonNode?> terms,
        IReadOnlyDictionary<string, string> sources, string from, params string[] to)
    {
        if (!terms.TryGetValue(from, out var value) || value is null) return;
        foreach (var field in to)
        {
            line[field] = value.DeepClone();
            if (sources.TryGetValue(from, out var source)) lineSources[field] = source;
        }
    }

    protected static IReadOnlyDictionary<string, ProductDefault> FromWorkspace(WorkspaceState workspace, string currency, string notional, string direction)
    {
        var d = LineDefaults.Of(workspace);
        var defaults = new Dictionary<string, ProductDefault>();
        if (d.Currency is { } c) defaults[currency] = new(c, Workspace);
        if (d.Notional is { } n) defaults[notional] = new(n, Workspace);
        if (d.Direction is { } dir) defaults[direction] = new(dir, Workspace);
        return defaults;
    }
}

public sealed class IrsRules(ConventionRegistry conventions, Func<RatesWorkspaces> workspaces) : RatesProductRules(conventions, workspaces)
{
    public override string ProductId => RatesProducts.Irs;

    public override IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given)
    {
        var defaults = FromWorkspace(workspace, "currency", "notional", "direction").ToDictionary();
        var index = Text(given, "index");
        // A currency left out follows an index typed, when that index names one currency; else the workspace's.
        var byIndex = index is null ? [] : Conventions.Profiles.Where(p => p.Index == index).ToList();
        if (Text(given, "currency") is null && byIndex is [var named]) defaults["currency"] = new(named.Currency, ProfileSource(named));
        var currency = Text(given, "currency") ?? defaults.GetValueOrDefault("currency")?.Value?.GetValue<string>();
        var profile = index is null ? OnlyProfileOf(currency) : ProfileOf(currency, index);
        if (index is null && profile is not null) defaults["index"] = new(profile.Index, ProfileSource(profile));
        if (profile?.Standard is { } standard)
            foreach (var (field, key) in new[] { ("fixedFrequency", "irs.fixedFrequency"), ("floatFrequency", "irs.floatFrequency") })
                if (standard.TryGetValue(key, out var value)) defaults[field] = new(value, ProfileSource(profile));
        // C47: at the money unless a spread is given: no spread on the floating leg, and the fixed leg at par unless a rate is.
        defaults["spread"] = new(0.0, "C47");
        return defaults;
    }

    protected override (JsonObject Terms, IReadOnlyDictionary<string, string> Sources) Line(IReadOnlyDictionary<string, JsonNode?> terms, IReadOnlyDictionary<string, string> sources)
    {
        var line = new JsonObject { [LineFields.Product] = "irs" };
        var lineSources = new Dictionary<string, string>();
        Put(line, lineSources, terms, sources, "end", LineFields.End);
        Put(line, lineSources, terms, sources, "start", LineFields.Start);
        Put(line, lineSources, terms, sources, "direction", LineFields.Direction);
        Put(line, lineSources, terms, sources, "risk", LineFields.Risk);
        Put(line, lineSources, terms, sources, "currency", LineFields.Leg(1, "currency"), LineFields.Leg(2, "currency"));
        Put(line, lineSources, terms, sources, "index", LineFields.Leg(1, "index"), LineFields.Leg(2, "index"));
        Put(line, lineSources, terms, sources, "notional", LineFields.Leg(1, "notional"), LineFields.Leg(2, "notional"));
        Put(line, lineSources, terms, sources, "fixedFrequency", LineFields.Leg(1, "paymentFrequency"));
        Put(line, lineSources, terms, sources, "floatFrequency", LineFields.Leg(2, "paymentFrequency"));
        if (terms.GetValueOrDefault("rate") is { } rate)
        {
            line[LineFields.Leg(1, "quoteKind")] = nameof(QuoteKind.FixedRatePercent);
            Put(line, lineSources, terms, sources, "rate", LineFields.Leg(1, "quote"));
        }
        if (terms.GetValueOrDefault("spread") is not null)
        {
            line[LineFields.Leg(2, "quoteKind")] = nameof(QuoteKind.FloatSpreadPercent);
            Put(line, lineSources, terms, sources, "spread", LineFields.Leg(2, "quote"));
            if (sources.TryGetValue("spread", out var s)) lineSources[LineFields.Leg(2, "quoteKind")] = s;
        }
        return (line, lineSources);
    }
}

public sealed class CcsRules(ConventionRegistry conventions, Func<RatesWorkspaces> workspaces) : RatesProductRules(conventions, workspaces)
{
    public override string ProductId => RatesProducts.Ccs;

    public override IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given)
    {
        // The workspace's currency and notional are leg 1's; leg 2's notional is never derived from a market rate (ADR-0008 §4).
        var defaults = FromWorkspace(workspace, "currency1", "notional1", "direction").ToDictionary();
        foreach (var n in new[] { 1, 2 })
        {
            var currency = Text(given, $"currency{n}") ?? defaults.GetValueOrDefault($"currency{n}")?.Value?.GetValue<string>();
            var index = Text(given, $"index{n}");
            var profile = index is null ? OnlyProfileOf(currency) : ProfileOf(currency, index);
            if (profile is null) continue;
            if (index is null) defaults[$"index{n}"] = new(profile.Index, ProfileSource(profile));
            if (profile.Standard?.TryGetValue("ccs.frequency", out var frequency) == true) defaults[$"frequency{n}"] = new(frequency, ProfileSource(profile));
        }
        return defaults;
    }

    protected override (JsonObject Terms, IReadOnlyDictionary<string, string> Sources) Line(IReadOnlyDictionary<string, JsonNode?> terms, IReadOnlyDictionary<string, string> sources)
    {
        var line = new JsonObject { [LineFields.Product] = "ccs" };
        var lineSources = new Dictionary<string, string>();
        Put(line, lineSources, terms, sources, "end", LineFields.End);
        Put(line, lineSources, terms, sources, "start", LineFields.Start);
        Put(line, lineSources, terms, sources, "direction", LineFields.Direction);
        Put(line, lineSources, terms, sources, "risk", LineFields.Risk);
        foreach (var n in new[] { 1, 2 })
        {
            Put(line, lineSources, terms, sources, $"currency{n}", LineFields.Leg(n, "currency"));
            Put(line, lineSources, terms, sources, $"index{n}", LineFields.Leg(n, "index"));
            Put(line, lineSources, terms, sources, $"notional{n}", LineFields.Leg(n, "notional"));
            Put(line, lineSources, terms, sources, $"frequency{n}", LineFields.Leg(n, "paymentFrequency"));
            if (terms.GetValueOrDefault($"spread{n}") is not null)
            {
                line[LineFields.Leg(n, "quoteKind")] = nameof(QuoteKind.FloatSpreadPercent);
                Put(line, lineSources, terms, sources, $"spread{n}", LineFields.Leg(n, "quote"));
            }
        }
        return (line, lineSources);
    }
}
