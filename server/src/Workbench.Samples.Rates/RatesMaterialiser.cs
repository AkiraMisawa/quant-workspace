using System.Globalization;
using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Rates;

/// <summary>A period as priced: the generated period with the user's cashflow edits on it, and which fields they set.</summary>
public sealed record EffectivePeriod(
    string Entity,
    DateOnly AccrualStart,
    DateOnly AccrualEnd,
    DateOnly PaymentDate,
    double Notional,
    double? Rate,
    double YearFraction,
    IReadOnlyList<string> UserFields);

public sealed record EffectiveLeg(LegTerms Terms, ConventionProfile Profile, IReadOnlyList<EffectivePeriod> Periods, IReadOnlyList<NotionalExchange> Exchanges);

/// <summary>
/// A line as the grid shows it and Price would submit it: its effective fields, its legs with the user's cashflow
/// edits applied, the leg solved at par, and every reason it cannot be priced, held edits included.
/// </summary>
public sealed record EffectiveLine(
    string Line,
    long CreatedSeq,
    IReadOnlyDictionary<string, JsonNode?> Fields,
    IReadOnlyList<string> UserFields,
    RatesLine? Terms,
    DateOnly? Spot,
    IReadOnlyList<EffectiveLeg> Legs,
    int? ParLeg,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Held)
{
    public bool Priceable => Issues.Count == 0 && Held.Count == 0 && Terms is not null;
}

public static class RatesMaterialiser
{
    /// <summary>A JSON number as written, whatever numeric type carried it. Anything else is a defect upstream.</summary>
    public static double Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"{node?.ToJsonString() ?? "null"} is not a number.");

    public static IReadOnlyList<EffectiveLine> Lines(WorkspaceState state, ConventionRegistry conventions)
    {
        var valuationDate = RatesEditRules.ValuationDate(state);
        return [.. state.Created.Values.Where(c => c.Scope == RatesEditRules.LinesScope).OrderBy(c => c.Seq)
            .Select(c => Line(state, c.Entity, valuationDate, conventions))];
    }

    public static EffectiveLine Line(WorkspaceState state, string line, DateOnly valuationDate, ConventionRegistry conventions)
    {
        var scope = RatesEditRules.SchedulePrefix + line;
        var fields = RatesEditRules.Fields(state, line);
        var userFields = state.Overrides.Keys.Where(t => t.Scope == RatesEditRules.LinesScope && t.Entity == line).Select(t => t.Field).Order().ToList();
        var held = state.Conflicts.Keys.Select(t => (t, "conflict")).Concat(state.Lost.Where(l => !l.Value.Acknowledged).Select(l => (l.Key, "lost")))
            .Where(x => x.Item1.Entity == line || x.Item1.Scope == scope)
            .Select(x => $"{x.Item1.Entity} {x.Item1.Field}: {x.Item2}")
            .Order(StringComparer.Ordinal)
            .ToList();
        var (resolution, issues) = RatesEditRules.Resolve(state, line, valuationDate, conventions);
        if (resolution is not { Priceable: true })
            return new(line, state.Created[line].Seq, fields, userFields, resolution?.Line, resolution?.Spot, [], null, issues, held);

        var cashflowEdits = state.Overrides.Values.Where(o => o.Target.Scope == scope).ToLookup(o => o.Target.Entity);
        var legs = new List<EffectiveLeg>();
        for (var n = 1; n <= resolution.Legs.Count; n++)
        {
            var leg = resolution.Legs[n - 1];
            var periods = leg.Schedule.Periods.Select(p =>
            {
                var entity = RatesEditRules.PeriodEntity(line, n, p);
                var edits = cashflowEdits[entity].ToDictionary(o => o.Target.Field, o => o.Value);
                DateOnly Date(string field, DateOnly generated) => edits.TryGetValue(field, out var v)
                    ? DateOnly.ParseExact(v!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture) : generated;
                var (start, end) = (Date("accrualStart", p.AccrualStart), Date("accrualEnd", p.AccrualEnd));
                var notional = edits.TryGetValue("notional", out var nv) ? Number(nv) : p.Notional;
                double? rate = edits.TryGetValue("rate", out var rv) ? (rv is null ? null : Number(rv)) : leg.Terms.Quote?.Value;
                return new EffectivePeriod(entity, start, end, Date("paymentDate", p.PaymentDate), notional, rate,
                    DateRules.YearFraction(start, end, leg.Profile.DayCount), [.. edits.Keys.Order()]);
            }).ToList();
            legs.Add(new(leg.Terms, leg.Profile, periods, leg.Schedule.Exchanges));
        }
        var dateIssues = legs.SelectMany(l => l.Periods).Where(p => p.AccrualEnd <= p.AccrualStart)
            .Select(p => $"{p.Entity}: the accrual end is not after its start.").ToList();
        return new(line, state.Created[line].Seq, fields, userFields, resolution.Line, resolution.Spot, legs, resolution.ParLeg, dateIssues, held);
    }
}
