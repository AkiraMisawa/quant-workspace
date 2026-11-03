using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workbench.Agent;

namespace Workbench.Samples.Rates;

/// <summary>
/// What the scripted agent understands of Rates (ADR-0007 §6): an IRS from the terms a request writes, and starting and
/// stopping ticking. Only stated terms are read; a missing one is left for the command to refuse by name, so the agent
/// asks (ADR-0005 §4). An index names its currency through the configured conventions; nothing else is inferred.
/// </summary>
public static class RatesScriptedRequests
{
    /// <summary>The AG-UI context entry the pane sends for an open Rates workspace (ADR-0007 §8).</summary>
    public const string ContextKind = "rates.workspace";

    public static IEnumerable<IScriptedRequest> All(ConventionRegistry conventions) =>
        [new CreateIrs(conventions), new LiveStart(), new LiveStop()];

    /// <summary>The workspace a request acts on: one made by an earlier call for this request, else the open one.</summary>
    static string? WorkspaceOf(ScriptedStep step)
    {
        foreach (var (capability, outputs) in step.Earlier.Reverse())
            if (capability == "rates.line.create" && outputs["line"]?["workspaceId"]?.GetValue<string>() is { } made) return made;
        return OpenWorkspace(step)?["workspaceId"]?.GetValue<string>();
    }

    static JsonObject? OpenWorkspace(ScriptedStep step)
    {
        foreach (var entry in step.Context)
        {
            JsonNode? value;
            try
            {
                value = JsonNode.Parse(entry.Value);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (value is JsonObject open && open["kind"]?.GetValue<string>() == ContextKind) return open;
        }
        return null;
    }

    sealed class CreateIrs(ConventionRegistry conventions) : IScriptedRequest
    {
        static readonly Regex Swap = new(@"\b(swaps?|irs)\b", RegexOptions.CultureInvariant);
        static readonly Regex Frequency = new(@"\b(fixed|float|floating)\s+(?:leg\s+)?(\d+m|annual|semi-annual|quarterly|monthly)\b", RegexOptions.CultureInvariant);
        static readonly Regex Tenor = new(@"\b(\d+)\s*-?\s*(y|yr|yrs|years?|months?)\b", RegexOptions.CultureInvariant);
        static readonly Regex Notional = new(@"\b(\d+(?:\.\d+)?)\s*(bn|b|billion|mm|m|million|k)\b|\b(\d{1,3}(?:,\d{3})+|\d{5,})\b", RegexOptions.CultureInvariant);
        static readonly Regex Rate = new(@"\b(?:fixed\s+)?rate\s+(?:of\s+)?(-?\d+(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);
        static readonly Regex Spread = new(@"\bspread\s+(?:of\s+)?(-?\d+(?:\.\d+)?)\s*%?|\b(zero|no)\s+spread\b", RegexOptions.CultureInvariant);
        static readonly Regex Date = new(@"\b(\d{4}-\d{2}-\d{2})\b", RegexOptions.CultureInvariant);
        static readonly Regex NewWorkspace = new(@"\bnew\s+workspace\b", RegexOptions.CultureInvariant);

        public string CapabilityId => "rates.line.create";
        public string Example => "make a 10y JPY TONA swap, pay fixed, 3bn, fixed 12m float 12m, spread 0";
        public string Hint => "for example \"pay fixed\", \"fixed 12m float 3m\", \"spread 0\" or \"rate 1.2%\", a valuation date such as 2026-10-27, or open a Rates workspace";

        public int? FindIn(string request) => Swap.Match(request) is { Success: true } m ? m.Index : null;

        public JsonObject Inputs(ScriptedStep step)
        {
            var text = step.Request;
            var terms = new JsonObject { [LineFields.Product] = "irs" };

            var open = NewWorkspace.IsMatch(text) ? null : OpenWorkspace(step)?["workspaceId"]?.GetValue<string>();
            if (open is not null) terms["workspaceId"] = open;
            else if (Date.Match(text) is { Success: true } date) terms["valuationDate"] = date.Groups[1].Value;
            text = Date.Replace(text, " ");

            foreach (Match m in Frequency.Matches(text))
                terms[LineFields.Leg(m.Groups[1].Value == "fixed" ? 1 : 2, "paymentFrequency")] = m.Groups[2].Value switch
                {
                    "annual" => "12m", "semi-annual" => "6m", "quarterly" => "3m", "monthly" => "1m", var written => written,
                };
            text = Frequency.Replace(text, " ");

            if (Rate.Match(text) is { Success: true } rate)
            {
                terms[LineFields.Leg(1, "quoteKind")] = nameof(QuoteKind.FixedRatePercent);
                terms[LineFields.Leg(1, "quote")] = Number(rate.Groups[1].Value);
                text = Rate.Replace(text, " ");
            }
            if (Spread.Match(text) is { Success: true } spread)
            {
                terms[LineFields.Leg(2, "quoteKind")] = nameof(QuoteKind.FloatSpreadPercent);
                terms[LineFields.Leg(2, "quote")] = spread.Groups[1].Success ? Number(spread.Groups[1].Value) : 0;
                text = Spread.Replace(text, " ");
            }

            if (Tenor.Match(text) is { Success: true } tenor)
            {
                terms[LineFields.End] = tenor.Groups[1].Value + (tenor.Groups[2].Value.StartsWith("mo", StringComparison.Ordinal) ? "m" : "y");
                text = Tenor.Replace(text, " ", 1);
            }

            // "pay fixed" pays leg 1, the fixed leg; "pay float" receives it.
            if (Regex.IsMatch(text, @"\bpay(s|ing)?\s+(float|floating)\b") || Regex.IsMatch(text, @"\breceiv(e|es|ing)\s+fixed\b"))
                terms[LineFields.Direction] = "receive";
            else if (Regex.IsMatch(text, @"\bpay(s|ing)?\b")) terms[LineFields.Direction] = "pay";
            else if (Regex.IsMatch(text, @"\breceiv(e|es|ing)\b")) terms[LineFields.Direction] = "receive";

            var profiles = conventions.Profiles.ToList();
            var currency = profiles.Select(p => p.Currency).Distinct(StringComparer.Ordinal).FirstOrDefault(c => Regex.IsMatch(text, $@"\b{c.ToLowerInvariant()}\b"));
            var index = profiles.FirstOrDefault(p => (currency is null || p.Currency == currency) && Regex.IsMatch(text, $@"\b{Regex.Escape(p.Index.ToLowerInvariant())}\b"));
            // An index names its currency; a currency alone does not name an index.
            currency ??= index?.Currency;

            double? notional = null;
            if (Notional.Match(text) is { Success: true } n)
                notional = n.Groups[1].Success
                    ? Number(n.Groups[1].Value) * n.Groups[2].Value switch { "bn" or "b" or "billion" => 1e9, "mm" or "m" or "million" => 1e6, _ => 1e3 }
                    : Number(n.Groups[3].Value.Replace(",", "", StringComparison.Ordinal));

            foreach (var leg in new[] { 1, 2 })
            {
                if (currency is not null) terms[LineFields.Leg(leg, "currency")] = currency;
                if (index is not null) terms[LineFields.Leg(leg, "index")] = index.Index;
                if (notional is not null) terms[LineFields.Leg(leg, "notional")] = notional;
            }
            return new JsonObject { ["terms"] = terms };
        }

        public string Done(JsonObject outputs)
        {
            var line = outputs["line"]!;
            return $"Created line {line["line"]} in workspace {line["workspaceId"]} (valuation date {line["valuationDate"]}, revision {line["revision"]}).";
        }

        static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    sealed class LiveStart : IScriptedRequest
    {
        static readonly Regex Start = new(@"(?<!\bstop\s+)\b(start\s+(ticking|live)|ticking|tick|live\s+start)\b", RegexOptions.CultureInvariant);

        public string CapabilityId => "rates.live.start";
        public string Example => "start ticking";
        public string Hint => "open the Rates workspace to tick, or create a line in the same request";
        public int? FindIn(string request) => Start.Match(request) is { Success: true } m ? m.Index : null;
        public JsonObject Inputs(ScriptedStep step) => Target(step);
        public string Done(JsonObject outputs) =>
            $"Workspace {outputs["subscription"]!["workspaceId"]} is ticking live (subscription {outputs["subscription"]!["subscriptionId"]}).";
    }

    sealed class LiveStop : IScriptedRequest
    {
        static readonly Regex Stop = new(@"\b(stop\s+(ticking|live)|live\s+stop)\b", RegexOptions.CultureInvariant);

        public string CapabilityId => "rates.live.stop";
        public string Example => "stop ticking";
        public string Hint => "open the Rates workspace to stop";
        public int? FindIn(string request) => Stop.Match(request) is { Success: true } m ? m.Index : null;
        public JsonObject Inputs(ScriptedStep step) => Target(step);
        public string Done(JsonObject outputs) => $"Workspace {outputs["subscription"]!["workspaceId"]} stopped ticking, for everyone watching it.";
    }

    static JsonObject Target(ScriptedStep step) =>
        new() { ["workspace"] = WorkspaceOf(step) is { } id ? new JsonObject { ["workspaceId"] = id } : new JsonObject() };
}
