using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Xva;

public static class XvaContracts
{
    public const string Version = "1.0.0";
    public const string AcceptedInput = "xva.accepted-input";
    public const string CaseResult = "xva.case-result";
    public const string PriceInput = "xva.price-input";
}

/// <summary>
/// Builds the accepted input Price freezes: every trade in Before or After with its effective values, the
/// credit inputs, and the copy of the overrides it priced. The Run keeps it; nothing re-derives it later.
/// </summary>
public static class AcceptedInput
{
    public static JsonObject Build(WorkspaceFreeze freeze)
    {
        var state = freeze.State;
        var source = (XvaSourceVersion)state.Source;
        var credit = XvaMaterialiser.Credit(state);
        var trades = XvaMaterialiser.Trades(state).Where(t => t.Before || t.After).ToList();
        var invalid = trades.Where(t => t.Issues.Count > 0).ToList();
        if (invalid.Count > 0)
            throw new WorkspaceRefused("InvalidInput", $"{invalid.Count} trade(s) to price have validation issues, first {invalid[0].TradeId}: {invalid[0].Issues[0]}");
        return new JsonObject
        {
            ["workspaceId"] = freeze.WorkspaceId,
            ["revision"] = freeze.Revision,
            ["context"] = freeze.Context.DeepClone(),
            ["sourceVersion"] = freeze.SourceVersion,
            ["profileVersion"] = XvaCatalog.Profile.ProfileVersion,
            ["registration"] = source.Registration.Key,
            ["agreement"] = source.Agreement.MaId,
            ["nettingSet"] = source.Agreement.NettingSetId,
            ["credit"] = JsonSerializer.SerializeToNode(credit, WireJson.Options),
            ["trades"] = JsonSerializer.SerializeToNode(trades, WireJson.Options),
            ["overrides"] = new JsonArray([.. freeze.Overrides.Select(o => new JsonObject
            {
                ["entity"] = o.Target.Entity, ["field"] = o.Target.Field, ["value"] = o.Value?.DeepClone(), ["baseVersion"] = o.BaseVersion, ["seq"] = o.Seq,
            })]),
        };
    }
}

/// <summary>
/// The fake analytics provider (slice 02). Its CVA and FVA are a deterministic toy formula over notional,
/// time to maturity and recovery, chosen only so that edits visibly move the figures. They are not a model of
/// anything and every result says so (executionMode simulated). Before and After are computed independently.
/// </summary>
public sealed class FakeXvaPrice : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.price", XvaContracts.Version, "Price CVA and FVA (simulated)",
        [new("acceptedInput", XvaContracts.AcceptedInput, XvaContracts.Version, Cardinality.One)],
        [new("result", XvaContracts.CaseResult, XvaContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public static readonly IReadOnlyList<DatasetColumn> Columns =
    [
        new("measure", DatasetColumnType.String, false),
        new("before", DatasetColumnType.Number, true),
        new("after", DatasetColumnType.Number, true),
        new("incremental", DatasetColumnType.Number, true),
        new("tradesBefore", DatasetColumnType.Integer, false),
        new("tradesAfter", DatasetColumnType.Integer, false),
    ];

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var reference = invocation.Inputs["acceptedInput"].Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", "The accepted input is required.");
        var input = invocation.Data.ReadRequired(reference);
        var valueDate = DateOnly.ParseExact(input["context"]!["valueDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var credit = input["credit"]!;
        // An empty CVA recovery is not zero: the CDS recovery applies (docs/03).
        var recovery = credit["cvaRecovery"]?.GetValue<decimal>() ?? credit["cdsRecovery"]!.GetValue<decimal>();
        var trades = input["trades"]!.AsArray().Select(t => t!).ToList();

        (decimal Cva, decimal Fva, int Count) Side(string side)
        {
            var members = trades.Where(t => t[side]!.GetValue<bool>()).ToList();
            decimal cva = 0, fva = 0;
            foreach (var t in members)
            {
                var years = (decimal)(DateOnly.ParseExact(t["maturity"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber - valueDate.DayNumber) / 365m;
                var exposure = Math.Abs(t["notional"]!.GetValue<decimal>()) * Math.Max(years, 0) * (t["currency"]!.GetValue<string>() == "JPY" ? 0.0001m : 0.015m);
                var uncollateralised = t["csa"]!.GetValue<string>().StartsWith("nocsa_", StringComparison.Ordinal);
                var curveFactor = t["curve"]!.GetValue<string>() == "usd_sofr" ? 1.1m : 1m;
                cva -= exposure * (1 - recovery) * (uncollateralised ? 0.02m : 0.004m) * curveFactor;
                fva -= exposure * (uncollateralised ? 0.006m : 0.0005m) * curveFactor;
            }
            return (Math.Round(cva, 2), Math.Round(fva, 2), members.Count);
        }

        var before = Side("before");
        var after = Side("after");
        JsonObject Row(string measure, decimal b, decimal a) => new()
        {
            ["measure"] = measure, ["before"] = b, ["after"] = a, ["incremental"] = a - b,
            ["tradesBefore"] = before.Count, ["tradesAfter"] = after.Count,
        };
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["result"] = Datasets.Create("CVA and FVA (simulated)", Columns,
                [Row("CVA", before.Cva, after.Cva), Row("FVA", before.Fva, after.Fva)],
                new(ExecutionMode.Simulated, DataOrigin.Synthetic, invocation.InvocationId)),
        });
    }
}

/// <summary>Price: freeze the revision the user saw, build its accepted input, retain it, and submit one Run.</summary>
public static class XvaPrice
{
    public static RunSubmission Submission(WorkspaceService workspaces, IDataStore store, string workspaceId, long expectedRevision, string idempotencyKey,
        string owner, RunOrigin origin = RunOrigin.Pricer, RunRelations? relations = null)
    {
        var freeze = workspaces.Freeze(workspaceId, expectedRevision, XvaMaterialiser.PricedEntities);
        var input = AcceptedInput.Build(freeze);
        var reference = store.Put($"xva-input-{freeze.WorkspaceId}-r{freeze.Revision}", XvaContracts.AcceptedInput, XvaContracts.Version, input);
        return new RunSubmission(XvaModule.Workflow(reference), freeze.Context,
            new JsonObject { ["workspaceId"] = freeze.WorkspaceId, ["revision"] = freeze.Revision }, idempotencyKey, owner, origin,
            (relations ?? RunRelations.None) with { WorkspaceId = freeze.WorkspaceId, WorkspaceRevision = freeze.Revision });
    }
}

/// <summary>Registers the XVA sample: its price capability, contracts, the price workflow and the result view.</summary>
public sealed class XvaModule : IWorkbenchModule
{
    public const string PriceWorkflow = "workflow.xva.price";

    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new FakeXvaPrice());
        builder.AddContract(new ContractDefinition(XvaContracts.PriceInput, XvaContracts.Version, "XVA price input", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
            },
            ["required"] = new JsonArray("workspaceId", "revision"),
            ["additionalProperties"] = false,
        }));
        builder.AddView(new ViewDefinition("view", CompositionValidator.SchemaVersion, "view.xva.result", XvaContracts.Version, "CVA and FVA",
            new ViewLayout(1, 1),
            [new ViewComponent("result-grid", ComponentKind.Grid, "CVA and FVA (simulated)", new WorkflowOutputData("result"),
                [new("measure", "Measure", ColumnFormat.Text), new("before", "Before", ColumnFormat.Number), new("after", "After", ColumnFormat.Number),
                 new("incremental", "Incremental", ColumnFormat.Number)], null)]));
    }

    /// <summary>The price workflow for one frozen accepted input. It pins the input by reference, so the Run carries it.</summary>
    public static WorkflowDefinition Workflow(TypedDataRef acceptedInput) => new("workflow", CompositionValidator.SchemaVersion, PriceWorkflow, XvaContracts.Version,
        "Price CVA and FVA", XvaContracts.PriceInput, XvaContracts.Version,
        [new WorkflowStep("price", "xva.price", XvaContracts.Version, [],
            new Dictionary<string, Binding> { ["acceptedInput"] = new DataReferenceBinding(acceptedInput) }, FailurePolicy.Stop)],
        new Dictionary<string, Binding> { ["result"] = new OutputBinding("price", "result") });
}
