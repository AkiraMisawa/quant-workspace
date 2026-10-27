using System.Globalization;
using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Xva;

// Synthetic XVA sources (slice 02). Every name, identifier and number here is made up; nothing is a real
// counterparty, agreement, curve or convention.

/// <summary>A counterparty registration. The code alone is not an identity: the legal entity is part of it.</summary>
public sealed record Registration(string Code, string LegalEntity, string Name)
{
    public string Key => $"{Code}/{LegalEntity}";
}

/// <summary>A master agreement with its netting set and CSAs. An uncollateralised one has none, only its sentinel.</summary>
public sealed record Agreement(string MaId, string NettingSetId, string RegistrationKey, IReadOnlyList<Csa> Csas, string? MainCsaId)
{
    public bool Collateralised => Csas.Count > 0;
    public string NoCsa => $"nocsa_{MaId}";
}

public sealed record Csa(string CsaId, string Currency, string Curve);

/// <summary>One acquired trade, as the source reports it for one value date.</summary>
public sealed record SourceTrade(
    string TradeId,
    bool IsNew,
    string Product,
    string Currency,
    decimal Notional,
    DateOnly Maturity,
    decimal? FixedRate,
    string? Schedule,
    decimal? LastFixing,
    string? Csa);

/// <summary>The configured baseline profile: the curve an uncollateralised trade starts on (docs/03 §2.4).</summary>
public sealed record BaselineProfile(string ProfileVersion, string UncollateralisedCurve);

public static class XvaCatalog
{
    public static readonly IReadOnlyList<Registration> Registrations =
    [
        new("ABC", "LE-01", "ABC Holdings (synthetic)"),
        new("ABC", "LE-02", "ABC Securities (synthetic)"),
        new("XYZ", "LE-03", "XYZ Industries (synthetic)"),
    ];

    public static readonly IReadOnlyList<string> Curves = ["jpy_ois", "usd_sofr", "eur_estr"];

    public static readonly IReadOnlyList<Agreement> Agreements =
    [
        new("MA-001", "NS-001", "ABC/LE-01", [new("CSA-A", "JPY", "jpy_ois"), new("CSA-B", "USD", "usd_sofr")], "CSA-A"),
        new("MA-002", "NS-002", "ABC/LE-02", [new("CSA-C", "USD", "usd_sofr")], "CSA-C"),
        new("MA-003", "NS-003", "XYZ/LE-03", [], null),
    ];

    public static readonly BaselineProfile Profile = new("profile-2026-10", "jpy_ois");

    public static Registration Find(string key) =>
        Registrations.SingleOrDefault(r => r.Key == key) ?? throw new ArgumentException($"No registration {key}.");

    public static Agreement AgreementOf(string registrationKey) =>
        Agreements.Single(a => a.RegistrationKey == registrationKey);
}

/// <summary>
/// What the synthetic source answers for one registration, value date and portfolio size. Deterministic: the
/// same request gives the same trades. A trade past its maturity is absent, and a trade's last fixing moves
/// when a fixing date has passed, so another value date is another version of the same trades.
/// </summary>
public sealed class XvaSourceVersion : ISourceVersion
{
    public Registration Registration { get; }
    public Agreement Agreement { get; }
    public DateOnly ValueDate { get; }
    public IReadOnlyList<SourceTrade> Existing { get; }
    public IReadOnlyList<SourceTrade> New { get; }
    public decimal CdsRecovery { get; }
    public decimal? CvaRecovery { get; }
    public string Version { get; }
    public JsonObject Descriptor => new() { ["registration"] = Registration.Key, ["valueDate"] = ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["existingCount"] = existingCount };

    readonly int existingCount;
    readonly Dictionary<string, SourceTrade> trades;

    public XvaSourceVersion(string registrationKey, DateOnly valueDate, int existingCount)
    {
        Registration = XvaCatalog.Find(registrationKey);
        Agreement = XvaCatalog.AgreementOf(registrationKey);
        ValueDate = valueDate;
        Existing = [.. Enumerable.Range(1, existingCount).Select(i => Generate(Registration, $"{Registration.Code}-{Registration.LegalEntity}-E{i:D5}", false, valueDate)).OfType<SourceTrade>()];
        this.existingCount = existingCount;
        New = [.. Enumerable.Range(1, 3).Select(i => Generate(Registration, $"DRAFT-{Registration.LegalEntity}-{i}", true, valueDate)).OfType<SourceTrade>()];
        trades = Existing.Concat(New).ToDictionary(t => t.TradeId);
        CdsRecovery = 0.4m;
        // One registration has an explicit empty CVA recovery: analytics then uses the CDS recovery (docs/03).
        CvaRecovery = Registration.LegalEntity == "LE-01" ? null : 0.35m;
        Version = $"{registrationKey}@{valueDate:yyyy-MM-dd}#{existingCount}";
    }

    /// <summary>Acquires the version a descriptor names again. The synthetic source is deterministic, so it is the same.</summary>
    public static XvaSourceVersion Resolve(JsonObject descriptor) => new(
        descriptor["registration"]!.GetValue<string>(),
        DateOnly.ParseExact(descriptor["valueDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        descriptor["existingCount"]!.GetValue<int>());

    public SourceTrade? Trade(string tradeId) => trades.GetValueOrDefault(tradeId);

    public Csa? CsaOf(string csaId) => Agreement.Csas.SingleOrDefault(c => c.CsaId == csaId);

    public SourceValue? Read(string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        switch (kind)
        {
            case Entity.Trade when trades.GetValueOrDefault(id) is { } t:
                return field switch
                {
                    "notional" => new(t.Notional),
                    "maturity" => new(t.Maturity.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    "fixedRate" => new(t.FixedRate),
                    "schedule" => new(t.Schedule),
                    "lastFixing" => new(t.LastFixing),
                    // A new trade has no CSA of its own: it takes the main CSA automatically.
                    "csa" => new(t.Csa),
                    // A trade's curve follows its CSA unless the user sets one.
                    "curve" => new((JsonNode?)null),
                    "before" => new(!t.IsNew),
                    "after" => new(true),
                    _ => null,
                };
            case Entity.Csa when CsaOf(id) is { } csa:
                return field == "curve" ? new(csa.Curve) : null;
            case Entity.Credit when id == Registration.Key:
                return field switch
                {
                    "cvaRecovery" => new(CvaRecovery),
                    "cdsRecovery" => new(CdsRecovery),
                    _ => null,
                };
            default:
                return null;
        }
    }

    static SourceTrade? Generate(Registration registration, string tradeId, bool isNew, DateOnly valueDate)
    {
        var seed = Seed(tradeId);
        var random = new Random(seed);
        var start = new DateOnly(2024, 1, 2).AddDays(random.Next(0, 900));
        var maturity = start.AddMonths(6 + random.Next(0, 9) * 6);
        if (isNew) maturity = valueDate.AddYears(5);
        else if (maturity <= valueDate) return null;
        var swap = random.NextDouble() < 0.7;
        var currency = random.NextDouble() < 0.6 ? "JPY" : "USD";
        var notional = Math.Round((decimal)(1 + random.Next(1, 100)) * (currency == "JPY" ? 100_000_000m : 1_000_000m), 0);
        var agreement = XvaCatalog.AgreementOf(registration.Key);
        string? csa = isNew ? null : agreement.Collateralised ? agreement.Csas[random.Next(agreement.Csas.Count)].CsaId : agreement.NoCsa;
        if (!swap)
            return new(tradeId, isNew, "FXForward", currency, notional, maturity, null, null, null, csa);
        var schedule = random.NextDouble() < 0.5 ? "6M" : "3M";
        var fixedRate = Math.Round((decimal)(0.001 + random.NextDouble() * 0.04), 5);
        // The last fixing is set on the latest fixing date not after the value date.
        var period = schedule == "6M" ? 182 : 91;
        var since = valueDate.DayNumber - start.DayNumber;
        var fixings = since < 0 ? 0 : since / period + 1;
        decimal? lastFixing = isNew ? null : Math.Round((decimal)(0.001 + new Random(seed ^ fixings).NextDouble() * 0.04), 5);
        return new(tradeId, isNew, "IRS", currency, notional, maturity, fixedRate, schedule, lastFixing, csa);
    }

    static int Seed(string text)
    {
        var hash = 17;
        foreach (var c in text) hash = unchecked(hash * 31 + c);
        return hash;
    }
}

/// <summary>Entity names in XVA edit targets: <c>trade:ID</c>, <c>csa:ID</c>, <c>credit:CODE/LE</c>.</summary>
public static class Entity
{
    public const string Trade = "trade";
    public const string Csa = "csa";
    public const string Credit = "credit";

    public static string Of(string kind, string id) => $"{kind}:{id}";

    public static (string Kind, string Id) Split(string entity)
    {
        var at = entity.IndexOf(':');
        if (at <= 0) throw new ArgumentException($"Entity '{entity}' has no kind.");
        return (entity[..at], entity[(at + 1)..]);
    }
}
