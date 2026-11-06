using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workbench.Agent;

namespace Workbench.Samples.Rates;

/// <summary>
/// What the scripted agent understands of Rates (ADR-0007 §6): a new workspace at a valuation date, an IRS from the terms
/// a request writes, and starting and stopping ticking. The IRS is made through the generic <c>entity.create</c> with the
/// words the command line takes (ADR-0008 §5), so what the request leaves out comes from the same confirmed defaults, and
/// what none supplies is refused by name and asked for. Only stated terms are read; nothing is inferred.
/// </summary>
public static class RatesScriptedRequests
{
    /// <summary>The AG-UI context entry the pane sends for an open Rates workspace (ADR-0007 §8).</summary>
    public const string ContextKind = "rates.workspace";

    public static IEnumerable<IScriptedRequest> All(ConventionRegistry conventions) =>
        [new NewWorkspace(), new CreateIrs(conventions), new LiveStart(), new LiveStop()];

    /// <summary>The workspace a request acts on: one made by an earlier call for this request, else the open one.</summary>
    static string? WorkspaceOf(ScriptedStep step)
    {
        foreach (var (capability, outputs) in step.Earlier.Reverse())
            if ((capability == "rates.workspace.create" ? outputs["workspace"] : capability == "entity.create" ? outputs["entity"] : null)?["workspaceId"]?.GetValue<string>() is { } made)
                return made;
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

    static readonly Regex Date = new(@"\b(\d{4}-\d{2}-\d{2})\b", RegexOptions.CultureInvariant);
    static readonly Regex NewWorkspacePhrase = new(@"\bnew\s+workspace\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// A new workspace, when the request asks for one or none is open and it gives a valuation date. It comes first, so
    /// the line the request goes on to make lands in it. Without a date it is refused, and the agent asks for one.
    /// </summary>
    sealed class NewWorkspace : IScriptedRequest
    {
        public string CapabilityId => "rates.workspace.create";
        public string Example => "a new workspace on 2026-10-27";
        public string Hint => "a valuation date such as 2026-10-27";
        public int? FindIn(string request) => NewWorkspacePhrase.IsMatch(request) || Date.IsMatch(request) ? -1 : null;
        public bool Wanted(ScriptedStep step) => NewWorkspacePhrase.IsMatch(step.Request) || WorkspaceOf(step) is null;
        public JsonObject Inputs(ScriptedStep step) => new()
        {
            ["request"] = Date.Match(step.Request) is { Success: true } date ? new JsonObject { ["valuationDate"] = date.Groups[1].Value } : new JsonObject(),
        };
        public string Done(JsonObject outputs) => $"Made workspace {outputs["workspace"]!["workspaceId"]} (valuation date {outputs["workspace"]!["valuationDate"]}).";
    }

    /// <summary>An IRS, as the words <c>new irs</c> takes: each stated term written as the command line would read it.</summary>
    sealed class CreateIrs(ConventionRegistry conventions) : IScriptedRequest
    {
        readonly HashSet<string> Choices = [.. conventions.Profiles.SelectMany(p => new[] { p.Currency, p.Index }).Select(w => w.ToLowerInvariant())];

        static readonly Regex Swap = new(@"\b(swaps?|irs)\b", RegexOptions.CultureInvariant);
        static readonly Regex Frequency = new(@"\b(fixed|float|floating)\s+(?:leg\s+)?(\d+m|annual|semi-annual|quarterly|monthly)\b", RegexOptions.CultureInvariant);
        static readonly Regex Tenor = new(@"\b(\d+)\s*-?\s*(y|yr|yrs|years?|months?)\b", RegexOptions.CultureInvariant);
        static readonly Regex Notional = new(@"\b(\d+(?:\.\d+)?)\s*(bn|b|billion|mm|m|million|k)\b|\b(\d{1,3}(?:,\d{3})+|\d{5,})\b", RegexOptions.CultureInvariant);
        static readonly Regex Rate = new(@"\b(?:fixed\s+)?rate\s+(?:of\s+)?(-?\d+(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);
        static readonly Regex Spread = new(@"\bspread\s+(?:of\s+)?(-?\d+(?:\.\d+)?)\s*%?|\b(zero|no)\s+spread\b", RegexOptions.CultureInvariant);
        static readonly Regex Word = new(@"[a-z]+", RegexOptions.CultureInvariant);

        public string CapabilityId => "entity.create";
        public string Example => "make a 10y JPY swap, pay fixed, 3bn";
        public string Hint => "for example \"pay fixed\", \"jpy\", \"3bn\", \"fixed 12m float 3m\" or \"rate 1.2%\", or set the workspace's defaults";

        public int? FindIn(string request) => Swap.Match(request) is { Success: true } m ? m.Index : null;

        public JsonObject Inputs(ScriptedStep step)
        {
            var text = Date.Replace(NewWorkspacePhrase.Replace(step.Request, " "), " ");
            var words = new List<string>();

            foreach (Match m in Frequency.Matches(text))
                words.Add($"{(m.Groups[1].Value == "fixed" ? "fixed" : "float")}=" + m.Groups[2].Value switch
                {
                    "annual" => "12m", "semi-annual" => "6m", "quarterly" => "3m", "monthly" => "1m", var written => written,
                });
            text = Frequency.Replace(text, " ");
            if (Rate.Match(text) is { Success: true } rate) { words.Add($"rate={rate.Groups[1].Value}%"); text = Rate.Replace(text, " "); }
            if (Spread.Match(text) is { Success: true } spread)
            {
                words.Add($"spread={(spread.Groups[1].Success ? spread.Groups[1].Value : "0")}%");
                text = Spread.Replace(text, " ");
            }
            if (Tenor.Match(text) is { Success: true } tenor)
            {
                words.Add(tenor.Groups[1].Value + (tenor.Groups[2].Value.StartsWith("mo", StringComparison.Ordinal) ? "m" : "y"));
                text = Tenor.Replace(text, " ", 1);
            }

            // "pay fixed" pays leg 1, the fixed leg; "pay float" receives it.
            if (Regex.IsMatch(text, @"\bpay(s|ing)?\s+(float|floating)\b") || Regex.IsMatch(text, @"\breceiv(e|es|ing)\s+fixed\b")) words.Add("receive");
            else if (Regex.IsMatch(text, @"\bpay(s|ing)?\b")) words.Add("pay");
            else if (Regex.IsMatch(text, @"\breceiv(e|es|ing)\b")) words.Add("receive");

            if (Notional.Match(text) is { Success: true } n)
            {
                // Written with its scale, as the command line reads it: m is months there, so millions are mm.
                words.Add(n.Groups[1].Success
                    ? n.Groups[1].Value + n.Groups[2].Value switch { "bn" or "b" or "billion" => "bn", "mm" or "m" or "million" => "mm", _ => "k" }
                    : n.Groups[3].Value.Replace(",", "", StringComparison.Ordinal) is var full && double.Parse(full, CultureInfo.InvariantCulture) is var amount
                        ? (amount / 1e6).ToString("R", CultureInfo.InvariantCulture) + "mm" : full);
                text = Notional.Replace(text, " ", 1);
            }

            // Currencies and indices are the description's own words: a word the request has that a choice reads is passed
            // on as written; the server decides what it is (ADR-0008 §6).
            words.AddRange(Word.Matches(text).Select(m => m.Value).Where(w => w.Length >= 3 && Choices.Contains(w)).Distinct(StringComparer.Ordinal));

            var request = new JsonObject { ["product"] = RatesProducts.Irs, ["terms"] = string.Join(" ", words) };
            if (WorkspaceOf(step) is { } workspace) request["workspaceId"] = workspace;
            return new JsonObject { ["request"] = request };
        }

        public string Done(JsonObject outputs)
        {
            var entity = outputs["entity"]!;
            var defaulted = entity["defaulted"]!.GetValue<string>();
            return $"Created line {entity["entity"]} in workspace {entity["workspaceId"]} (revision {entity["revision"]})."
                + (defaulted.Length == 0 ? "" : $" Defaulted: {defaulted}.");
        }
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
