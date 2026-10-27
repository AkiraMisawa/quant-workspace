using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Rates;

/// <summary>A line's terms as flat fields, the way the grid edits them (C40, C43). Names are the wire contract.</summary>
public static class LineFields
{
    public const string Product = "product", Start = "start", End = "end", Direction = "direction",
        Collateral = "collateral", MandatoryBreak = "mandatoryBreak", Risk = "risk";

    public static readonly string[] LegBasic =
        ["currency", "index", "notional", "quoteKind", "quote", "paymentFrequency", "resetFrequency", "floating"];

    public static readonly string[] LegDetail =
        ["dayCount", "adjustment", "dailyAverage", "observationShift", "lookback", "paymentDelay", "inArrears", "gearing", "currentReset"];

    public static string Leg(int n, string field) => $"leg{n}.{field}";

    public static IEnumerable<string> All =>
        new[] { Product, Start, End, Direction, Collateral, MandatoryBreak, Risk }
            .Concat(new[] { 1, 2 }.SelectMany(n => LegBasic.Concat(LegDetail).Select(f => Leg(n, f))));

    /// <summary>A new line of a product: every field present and empty but the product, its legs' kinds, pay and no risk.</summary>
    public static JsonObject Template(RatesProduct product)
    {
        var fields = new JsonObject();
        foreach (var name in All) fields[name] = null;
        fields[Product] = product == RatesProduct.Irs ? "irs" : "ccs";
        fields[Direction] = "pay";
        fields[Risk] = false;
        fields[Leg(1, "floating")] = product == RatesProduct.Ccs;
        fields[Leg(2, "floating")] = true;
        return fields;
    }
}

/// <summary>Reads a line's effective fields into its typed terms, or the reasons it cannot (an empty required field).</summary>
public static class LineReader
{
    public static (RatesLine? Line, IReadOnlyList<string> Issues) Read(string rowKey, IReadOnlyDictionary<string, JsonNode?> f)
    {
        var issues = new List<string>();
        string? Text(string name) => f.GetValueOrDefault(name) is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        // A number arrives as whatever JSON type the client sent it in; read it as written, and refuse anything else.
        double? Number(string name)
        {
            if (f.GetValueOrDefault(name) is not JsonValue v) return null;
            if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number) return double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);
            issues.Add($"{name} is not a number.");
            return null;
        }
        int? Whole(string name)
        {
            if (Number(name) is not { } d) return null;
            if (d == Math.Floor(d) && Math.Abs(d) < int.MaxValue) return (int)d;
            issues.Add($"{name} is not a whole number.");
            return null;
        }
        bool? Flag(string name) => f.GetValueOrDefault(name) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        T Required<T>(T? value, string what) where T : class { if (value is null) issues.Add($"{what} is empty."); return value!; }

        DateOrTenor? When(string name)
        {
            if (Text(name) is not { } text) return null;
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return DateOrTenor.On(date);
            if (Tenor.TryParse(text, out var tenor)) return DateOrTenor.After(tenor);
            issues.Add($"'{text}' in {name} is neither a date (yyyy-MM-dd) nor a tenor.");
            return null;
        }

        Tenor? Period(string name)
        {
            if (Text(name) is not { } text) return null;
            if (Tenor.TryParse(text, out var tenor)) return tenor;
            issues.Add($"'{text}' in {name} is not a tenor.");
            return null;
        }

        TEnum? Choice<TEnum>(string name) where TEnum : struct, Enum
        {
            if (Text(name) is not { } text) return null;
            if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) && !int.TryParse(text, out _)) return value;
            issues.Add($"'{text}' in {name} is not one of {string.Join(", ", Enum.GetNames<TEnum>())}.");
            return null;
        }

        LegTerms? Leg(int n)
        {
            string L(string field) => LineFields.Leg(n, field);
            var kind = Choice<QuoteKind>(L("quoteKind"));
            var quote = Number(L("quote"));
            if (kind is null != quote is null) issues.Add($"Leg {n}: a quote needs both its kind and its value, or neither for par.");
            var pay = Period(L("paymentFrequency"));
            if (pay is null && Text(L("paymentFrequency")) is null) issues.Add($"Leg {n}: the payment frequency is empty.");
            var currency = Required(Text(L("currency")), $"Leg {n}'s currency");
            var index = Required(Text(L("index")), $"Leg {n}'s index");
            if (pay is null || currency is null || index is null) return null;
            return new LegTerms(currency, index, Number(L("notional")), kind is { } k && quote is { } q ? new LegQuote(k, q) : null,
                pay.Value, Period(L("resetFrequency")), Flag(L("floating")) ?? false)
            {
                Detail = new LegDetail(Choice<DayCountBasis>(L("dayCount")), Choice<BusinessDayAdjustment>(L("adjustment")),
                    Flag(L("dailyAverage")), Whole(L("observationShift")), Whole(L("lookback")), Whole(L("paymentDelay")),
                    Flag(L("inArrears")), Number(L("gearing")), Number(L("currentReset"))),
            };
        }

        var product = Choice<RatesProduct>(LineFields.Product);
        var direction = Choice<Direction>(LineFields.Direction);
        var start = When(LineFields.Start);
        var end = When(LineFields.End);
        if (end is null && Text(LineFields.End) is null) issues.Add("The end is empty.");
        var breakDate = Text(LineFields.MandatoryBreak) is { } b
            ? DateOnly.TryParseExact(b, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Refuse<DateOnly>($"'{b}' in mandatoryBreak is not a date.")
            : (DateOnly?)null;
        var (leg1, leg2) = (Leg(1), Leg(2));
        if (product is null) issues.Add("The product is empty.");
        if (direction is null) issues.Add("The direction is empty.");
        if (issues.Count > 0) return (null, issues);
        return (new RatesLine(rowKey, product!.Value, start, end!, direction!.Value, leg1!, leg2!, Text(LineFields.Collateral), breakDate, Flag(LineFields.Risk) ?? false), []);

        DateOnly? Refuse<T>(string issue) { issues.Add(issue); return null; }
    }
}

/// <summary>
/// The Rates module's edits on the generic workspace (ADR-0004): lines are entities the user creates in the
/// <c>lines</c> scope; each line derives its standard schedule in the scope <c>schedule:{line}</c>, whose periods
/// are identified by leg and unadjusted dates, so a cashflow edit stays on its period while the term moves.
/// </summary>
public sealed class RatesEditRules(ConventionRegistry conventions) : IEditRules
{
    public const string App = "rates";
    public const string LinesScope = "lines";
    public const string SchedulePrefix = "schedule:";

    public static readonly string[] PeriodFields = ["accrualStart", "accrualEnd", "paymentDate", "notional", "rate"];

    public string KindOf(EditTarget target) => target.Scope.StartsWith(SchedulePrefix, StringComparison.Ordinal) ? "rates.cashflow" : "rates.line";

    // A period's dates are compared together: one of them moving changes what the others meant.
    public IReadOnlyList<string> ConflictGroup(EditTarget target) =>
        target.Field is "accrualStart" or "accrualEnd" or "paymentDate" ? ["accrualStart", "accrualEnd", "paymentDate"] : [target.Field];

    public IReadOnlyDictionary<string, ISourceVersion> Derive(WorkspaceState state)
    {
        var valuationDate = ValuationDate(state);
        return state.Created.Values.Where(c => c.Scope == LinesScope).ToDictionary(
            c => SchedulePrefix + c.Entity,
            c => (ISourceVersion)ScheduleSource.Of(c.Entity, Resolve(state, c.Entity, valuationDate, conventions)));
    }

    public static DateOnly ValuationDate(WorkspaceState state) =>
        DateOnly.ParseExact(state.Context["valuationDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A line's effective fields: its Create values with the user's overrides on them.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> Fields(WorkspaceState state, string line)
    {
        var created = state.Created[line];
        var fields = created.Fields.ToDictionary(p => p.Key, p => p.Value);
        foreach (var o in state.Overrides.Values.Where(o => o.Target.Entity == line && o.Target.Scope == LinesScope))
            fields[o.Target.Field] = o.Value;
        return fields;
    }

    public static (LineResolution? Resolution, IReadOnlyList<string> Issues) Resolve(WorkspaceState state, string line, DateOnly valuationDate, ConventionRegistry conventions)
    {
        var (terms, issues) = LineReader.Read(line, Fields(state, line));
        if (terms is null) return (null, issues);
        var resolution = LineResolver.Resolve(terms, valuationDate, conventions);
        return (resolution, resolution.Issues);
    }

    public static string PeriodEntity(string line, int leg, CashflowPeriod p) =>
        $"{line}/leg{leg}/{p.UnadjustedStart:yyyy-MM-dd}..{p.UnadjustedEnd:yyyy-MM-dd}";
}

/// <summary>One line's generated schedule read as a source (ADR-0004). A line that cannot be resolved derives no periods.</summary>
public sealed class ScheduleSource : ISourceVersion
{
    readonly Dictionary<string, Dictionary<string, JsonNode?>> periods;

    ScheduleSource(string version, Dictionary<string, Dictionary<string, JsonNode?>> periods) => (Version, this.periods) = (version, periods);

    public string Version { get; }
    public JsonObject Descriptor => [];

    public SourceValue? Read(string entity, string field) =>
        periods.TryGetValue(entity, out var fields) && fields.TryGetValue(field, out var value) ? new SourceValue(value?.DeepClone()) : null;

    public static ScheduleSource Of(string line, (LineResolution? Resolution, IReadOnlyList<string> Issues) resolved)
    {
        var periods = new Dictionary<string, Dictionary<string, JsonNode?>>();
        if (resolved.Resolution is { Priceable: true } resolution)
            for (var n = 1; n <= resolution.Legs.Count; n++)
            {
                var leg = resolution.Legs[n - 1];
                foreach (var p in leg.Schedule.Periods)
                    periods[RatesEditRules.PeriodEntity(line, n, p)] = new()
                    {
                        ["accrualStart"] = p.AccrualStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["accrualEnd"] = p.AccrualEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["paymentDate"] = p.PaymentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["notional"] = p.Notional,
                        ["rate"] = leg.Terms.Quote?.Value,
                    };
            }
        // The version is the content: the same terms, profiles and generator derive the same version.
        var content = new StringBuilder();
        foreach (var (entity, fields) in periods.OrderBy(p => p.Key, StringComparer.Ordinal))
            content.Append(entity).Append('|').AppendJoin('|', fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value?.ToJsonString()}")).Append('\n');
        if (resolved.Resolution is { } r)
            content.AppendJoin(',', r.Legs.Select(l => $"{l.Profile.Id}@{l.Profile.Version}/{l.Schedule.GeneratorVersion}"));
        content.AppendJoin(';', resolved.Issues);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())))[..16];
        return new ScheduleSource($"{line}:{hash}", periods);
    }
}
