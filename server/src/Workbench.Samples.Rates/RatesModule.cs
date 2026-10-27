using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Rates;

public static class RatesContracts
{
    public const string Version = "1.0.0";
    public const string AcceptedInput = "rates.accepted-input";
    public const string LineResult = "rates.line-result";
    public const string Risk = "rates.risk";
    public const string PriceInput = "rates.price-input";
}

/// <summary>
/// The input a Rates Run keeps (docs/07 §5): the priced lines with their effective legs, the lines left out and
/// why, the valuation date and the fixed market snapshot. Nothing re-derives it later; the provider reads it as is.
/// </summary>
public static class RatesAcceptedInput
{
    /// <summary>The wire options with nulls written: an empty quote is part of the input, not a missing property.</summary>
    static readonly JsonSerializerOptions Json = new(WireJson.Options) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    /// <summary>
    /// The accepted input. A Price passes the frozen overrides; a capture passes none, because the workspace may have
    /// moved on since its frame, and the append-only log at the frame's revision is what explains its edits.
    /// </summary>
    public static JsonObject Build(string workspaceId, long revision, JsonObject context, MarketUpdate market, LiveInput input,
        IEnumerable<Override>? overrides)
    {
        var built = new JsonObject
        {
            ["workspaceId"] = workspaceId,
            ["revision"] = revision,
            ["context"] = context.DeepClone(),
            ["valuationDate"] = input.ValuationDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["market"] = JsonSerializer.SerializeToNode(market, Json),
            ["lines"] = JsonSerializer.SerializeToNode(input.Lines, Json),
            ["notPriced"] = JsonSerializer.SerializeToNode(input.NotPriced, Json),
        };
        if (overrides is not null)
            built["overrides"] = new JsonArray([.. overrides.Select(o => new JsonObject
            {
                ["scope"] = o.Target.Scope, ["entity"] = o.Target.Entity, ["field"] = o.Target.Field, ["value"] = o.Value?.DeepClone(),
                ["baseVersion"] = o.BaseVersion, ["seq"] = o.Seq,
            })]);
        return built;
    }

    /// <summary>The input of one revision: its priceable lines, and every other line with the reasons it is not priced.</summary>
    public static LiveInput Of(WorkspaceState state, ConventionRegistry conventions)
    {
        var lines = RatesMaterialiser.Lines(state, conventions);
        return new LiveInput(state.Revision, RatesEditRules.ValuationDate(state),
            [.. lines.Where(l => l.Priceable).Select(l => PricingLines.From(l, state.Revision))],
            [.. lines.Where(l => !l.Priceable).Select(l => new NotPricedLine(l.Line, [.. l.Issues, .. l.Held]))]);
    }

    public static (MarketUpdate Market, DateOnly ValuationDate, IReadOnlyList<PricingLine> Lines, IReadOnlyList<NotPricedLine> NotPriced) Read(JsonNode input) =>
        (input["market"].Deserialize<MarketUpdate>(Json) ?? throw new CapabilityFailure("InvalidInput", "The accepted input names no market."),
         DateOnly.ParseExact(input["valuationDate"]!.GetValue<string>(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
         input["lines"].Deserialize<List<PricingLine>>(Json) ?? throw new CapabilityFailure("InvalidInput", "The accepted input has no lines."),
         input["notPriced"].Deserialize<List<NotPricedLine>>(Json) ?? throw new CapabilityFailure("InvalidInput", "The accepted input has no list of lines left out."));
}

/// <summary>The figures of one calculation as datasets: a row per line, and a row per risk factor of a line asked for risk.</summary>
public static class RatesResults
{
    public static readonly IReadOnlyList<DatasetColumn> LineColumns =
    [
        new("line", DatasetColumnType.String, false),
        new("status", DatasetColumnType.String, false),
        new("pv", DatasetColumnType.Number, true),
        new("pvCurrency", DatasetColumnType.String, true),
        new("parMeasure", DatasetColumnType.String, true),
        new("parQuote", DatasetColumnType.Number, true),
        new("parLeg", DatasetColumnType.Integer, true),
        new("message", DatasetColumnType.String, true),
    ];

    public static readonly IReadOnlyList<DatasetColumn> RiskColumns =
    [
        new("line", DatasetColumnType.String, false),
        new("measure", DatasetColumnType.String, false),
        new("value", DatasetColumnType.Number, false),
        new("unit", DatasetColumnType.String, false),
    ];

    public static (JsonObject Lines, JsonObject Risk) Datasets(RatesBatchResult result, IReadOnlyList<PricingLine> lines,
        IReadOnlyList<NotPricedLine> notPriced, MarketUpdate market, string invocationId)
    {
        var provenance = new DatasetProvenance(
            result.CalculationMode == FakeRatesPricing.Mode ? ExecutionMode.Simulated : ExecutionMode.Actual,
            market.Origin == SyntheticMarket.Origin ? DataOrigin.Synthetic : DataOrigin.Source, invocationId);
        var byLine = result.Rows.ToDictionary(r => r.RowKey);
        var rows = new List<JsonObject>();
        var risk = new List<JsonObject>();
        foreach (var line in lines)
        {
            if (!byLine.TryGetValue(line.RowKey, out var row))
            {
                // A provider that answers for fewer lines than it was given has failed those lines; nothing stands in.
                rows.Add(new() { ["line"] = line.RowKey, ["status"] = "failed", ["message"] = "The provider returned no result for this line." });
                continue;
            }
            if (row.Failure is not null)
            {
                rows.Add(new() { ["line"] = line.RowKey, ["status"] = "failed", ["message"] = row.Failure });
                continue;
            }
            var pv = row.Measures[Measures.Pv];
            var o = new JsonObject { ["line"] = line.RowKey, ["status"] = "priced", ["pv"] = pv.Value, ["pvCurrency"] = pv.Unit };
            foreach (var name in new[] { Measures.ParRate, Measures.FairSpread })
                if (row.Measures.TryGetValue(name, out var par))
                {
                    o["parMeasure"] = name;
                    o["parQuote"] = par.Value;
                    o["parLeg"] = line.ParLeg;
                }
            rows.Add(o);
            foreach (var (name, m) in row.Measures.Where(m => m.Key.StartsWith("delta:", StringComparison.Ordinal)).OrderBy(m => m.Key, StringComparer.Ordinal))
                risk.Add(new() { ["line"] = line.RowKey, ["measure"] = name, ["value"] = m.Value, ["unit"] = m.Unit });
        }
        foreach (var left in notPriced)
            rows.Add(new() { ["line"] = left.Line, ["status"] = "notPriced", ["message"] = string.Join("; ", left.Reasons) });
        var title = $"Rates lines ({result.CalculationMode}, {market.SnapshotId})";
        return (global::Workbench.Composition.Application.Datasets.Create(title, LineColumns, rows, provenance),
                global::Workbench.Composition.Application.Datasets.Create($"Rates risk ({result.CalculationMode}, {market.SnapshotId})", RiskColumns, risk, provenance));
    }
}

/// <summary>
/// Prices a retained accepted input against the snapshot it names, through the configured provider. A manual
/// price and a live batch call the same provider with the same input shape (docs/07 acceptance 4).
/// </summary>
public sealed class RatesPrice(IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.price", RatesContracts.Version, "Price rates lines",
        [new("acceptedInput", RatesContracts.AcceptedInput, RatesContracts.Version, Cardinality.One)],
        [new("result", RatesContracts.LineResult, RatesContracts.Version, Cardinality.One),
         new("risk", RatesContracts.Risk, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public async Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var input = invocation.Data.ReadRequired(Reference(invocation, "acceptedInput"));
        var (market, valuationDate, lines, notPriced) = RatesAcceptedInput.Read(input);
        var snapshot = resolveMarket(market.SnapshotId)
            ?? throw new CapabilityFailure("MarketUnavailable", $"Market snapshot {market.SnapshotId} cannot be resolved.");
        var revision = input["revision"]!.GetValue<long>();
        var result = await provider.PriceBatchAsync(new RatesBatchInput(revision, valuationDate, snapshot, lines), cancellationToken);
        var (rows, risk) = RatesResults.Datasets(result, lines, notPriced, market, invocation.InvocationId);
        return new Dictionary<string, JsonNode?> { ["result"] = rows, ["risk"] = risk };
    }

    internal static TypedDataRef Reference(CapabilityInvocation invocation, string name) =>
        invocation.Inputs[name].Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", $"The input {name} is required.");
}

/// <summary>
/// Saves a live frame as a Run (docs/07 §3, "Capture result"): its figures are the ones the user saw, read back as
/// they were retained, and are not calculated again; the frame's own market stays on them.
/// </summary>
public sealed class RatesCapture : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.capture", RatesContracts.Version, "Capture a live rates frame",
        [new("acceptedInput", RatesContracts.AcceptedInput, RatesContracts.Version, Cardinality.One),
         new("frame", RatesContracts.LineResult, RatesContracts.Version, Cardinality.One),
         new("frameRisk", RatesContracts.Risk, RatesContracts.Version, Cardinality.One)],
        [new("result", RatesContracts.LineResult, RatesContracts.Version, Cardinality.One),
         new("risk", RatesContracts.Risk, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.PersistAnalysis]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["result"] = invocation.Data.ReadRequired(RatesPrice.Reference(invocation, "frame")).DeepClone(),
            ["risk"] = invocation.Data.ReadRequired(RatesPrice.Reference(invocation, "frameRisk")).DeepClone(),
        });
}

/// <summary>Registers the Rates sample: its capabilities, contracts, workflows, history projection and result view.</summary>
public sealed class RatesModule(IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket) : IWorkbenchModule
{
    public const string PriceWorkflow = "workflow.rates.price";
    public const string CaptureWorkflow = "workflow.rates.capture";

    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new RatesPrice(provider, resolveMarket));
        builder.AddCapability(new RatesCapture());
        builder.AddHistoryProjection(new RatesHistoryProjection());
        builder.AddContract(new ContractDefinition(RatesContracts.PriceInput, RatesContracts.Version, "Rates price input", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
                ["marketSnapshotId"] = new JsonObject { ["type"] = "string", ["title"] = "Market snapshot" },
                ["subscriptionId"] = new JsonObject { ["type"] = "string", ["title"] = "Live subscription" },
                ["subscriptionRevision"] = new JsonObject { ["type"] = "integer", ["title"] = "Subscription revision" },
            },
            ["required"] = new JsonArray("workspaceId", "revision", "marketSnapshotId"),
            ["additionalProperties"] = false,
        }));
        builder.AddView(new ViewDefinition("view", CompositionValidator.SchemaVersion, "view.rates.result", RatesContracts.Version, "Rates lines",
            new ViewLayout(2, 1),
            [new ViewComponent("lines", ComponentKind.Grid, "Lines", new WorkflowOutputData("result"),
                [new("line", "Line", ColumnFormat.Text), new("status", "Status", ColumnFormat.Status), new("pv", "PV", ColumnFormat.Currency, "pvCurrency"),
                 new("parMeasure", "Solved", ColumnFormat.Text), new("parQuote", "Par / fair (%)", ColumnFormat.Number), new("message", "Message", ColumnFormat.Text)], null),
             new ViewComponent("risk", ComponentKind.Grid, "Risk", new WorkflowOutputData("risk"),
                [new("line", "Line", ColumnFormat.Text), new("measure", "Factor", ColumnFormat.Text), new("value", "Value", ColumnFormat.Number),
                 new("unit", "Unit", ColumnFormat.Text)], null)]));
    }

    public static WorkflowDefinition Price(TypedDataRef acceptedInput) => new("workflow", CompositionValidator.SchemaVersion, PriceWorkflow, RatesContracts.Version,
        "Price rates lines", RatesContracts.PriceInput, RatesContracts.Version,
        [new WorkflowStep("price", "rates.price", RatesContracts.Version, [],
            new Dictionary<string, Binding> { ["acceptedInput"] = new DataReferenceBinding(acceptedInput) }, FailurePolicy.Stop)],
        new Dictionary<string, Binding> { ["result"] = new OutputBinding("price", "result"), ["risk"] = new OutputBinding("price", "risk") });

    public static WorkflowDefinition Capture(TypedDataRef acceptedInput, TypedDataRef frame, TypedDataRef frameRisk) => new("workflow",
        CompositionValidator.SchemaVersion, CaptureWorkflow, RatesContracts.Version, "Capture a live rates frame", RatesContracts.PriceInput, RatesContracts.Version,
        [new WorkflowStep("capture", "rates.capture", RatesContracts.Version, [],
            new Dictionary<string, Binding>
            {
                ["acceptedInput"] = new DataReferenceBinding(acceptedInput),
                ["frame"] = new DataReferenceBinding(frame),
                ["frameRisk"] = new DataReferenceBinding(frameRisk),
            }, FailurePolicy.Stop)],
        new Dictionary<string, Binding> { ["result"] = new OutputBinding("capture", "result"), ["risk"] = new OutputBinding("capture", "risk") });
}

/// <summary>
/// Rates' view of the Pricing DB (docs/05 §7): one row per Run, with its valuation date, market snapshot, how it
/// was made, and how many lines it priced, failed and left out. Figures stay in the Run's result; a sum of PVs
/// across lines and currencies would be a number nobody asked for.
/// </summary>
public sealed class RatesHistoryProjection : IHistoryProjection
{
    public string Domain => RatesEditRules.App;
    public string Title => "Rates";
    public IReadOnlyList<string> ResultContracts { get; } = [RatesContracts.LineResult];

    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("valuationDate", DatasetColumnType.String, true),
        new("kind", DatasetColumnType.String, true),
        new("marketSnapshot", DatasetColumnType.String, true),
        new("priced", DatasetColumnType.Integer, true),
        new("failed", DatasetColumnType.Integer, true),
        new("notPriced", DatasetColumnType.Integer, true),
        new("executionMode", DatasetColumnType.String, true),
    ];

    public JsonObject Project(RunRecord run, IDataReader data)
    {
        var row = new JsonObject
        {
            ["valuationDate"] = run.Context["valuationDate"]?.DeepClone(),
            ["kind"] = run.Workflow.DefinitionId == RatesModule.CaptureWorkflow ? "capture" : "price",
            ["marketSnapshot"] = run.Input["marketSnapshotId"]?.DeepClone(),
        };
        if (!run.Outputs.TryGetValue("result", out var reference) || data.Read(reference) is not DataLookup.Found found)
            return row;
        var statuses = found.Content["rows"]!.AsArray().Select(r => r!["status"]!.GetValue<string>()).ToList();
        row["priced"] = statuses.Count(s => s == "priced");
        row["failed"] = statuses.Count(s => s == "failed");
        row["notPriced"] = statuses.Count(s => s == "notPriced");
        row["executionMode"] = found.Content["provenance"]?["executionMode"]?.DeepClone();
        return row;
    }
}
