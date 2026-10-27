using System.Globalization;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Rates;

public static class RatesCommandContracts
{
    public const string LineTerms = "rates.line-terms";
    public const string LineCreated = "rates.line-created";
    public const string LiveTarget = "rates.live-target";
    public const string LiveSubscription = "rates.live-subscription";

    /// <summary>
    /// The terms a line is created with: the grid's own field names (C40, C43), so a line the AI makes is the line the
    /// page shows. The basic terms of both legs are required; nothing has a default the user did not choose (ADR-0005).
    /// </summary>
    public static JsonObject LineTermsSchema()
    {
        static JsonObject Text(string title) => new() { ["type"] = "string", ["title"] = title };
        static JsonObject Date(string title) => new() { ["type"] = "string", ["format"] = "date", ["title"] = title };
        static JsonObject Number(string title) => new() { ["type"] = "number", ["title"] = title };
        static JsonObject Whole(string title) => new() { ["type"] = "integer", ["minimum"] = 0, ["title"] = title };
        static JsonObject Flag(string title) => new() { ["type"] = "boolean", ["title"] = title };
        static JsonObject OneOf(string title, IEnumerable<string> names) => new() { ["type"] = "string", ["title"] = title, ["enum"] = new JsonArray([.. names.Select(n => JsonValue.Create(n))]) };

        var properties = new JsonObject
        {
            ["workspaceId"] = Text("Workspace (empty: a new one)"),
            ["valuationDate"] = Date("Valuation date of a new workspace"),
            [LineFields.Product] = OneOf("Product", ["irs", "ccs"]),
            [LineFields.Start] = new JsonObject { ["type"] = "string", ["title"] = "Start: a date or a tenor from spot (empty: spot)" },
            [LineFields.End] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "End: a date or a tenor from the start" },
            [LineFields.Direction] = OneOf("Pay or receive leg 1", ["pay", "receive"]),
            [LineFields.Collateral] = Text("Collateral"),
            [LineFields.MandatoryBreak] = Date("Mandatory break"),
            [LineFields.Risk] = Flag("Risk"),
        };
        foreach (var n in new[] { 1, 2 })
        {
            string L(string f) => LineFields.Leg(n, f);
            properties[L("currency")] = new JsonObject { ["type"] = "string", ["minLength"] = 3, ["title"] = $"Leg {n} currency" };
            properties[L("index")] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = $"Leg {n} index (names its conventions)" };
            properties[L("notional")] = Number($"Leg {n} notional");
            properties[L("quoteKind")] = OneOf($"Leg {n} quote kind (empty with the quote: solved at par)", Enum.GetNames<QuoteKind>());
            properties[L("quote")] = Number($"Leg {n} quote");
            properties[L("paymentFrequency")] = new JsonObject { ["type"] = "string", ["minLength"] = 2, ["title"] = $"Leg {n} payment frequency (a tenor)" };
            properties[L("resetFrequency")] = Text($"Leg {n} reset frequency (a tenor)");
            properties[L("dayCount")] = OneOf($"Leg {n} day count", Enum.GetNames<DayCountBasis>());
            properties[L("adjustment")] = OneOf($"Leg {n} accrual adjustment", Enum.GetNames<BusinessDayAdjustment>());
            properties[L("dailyAverage")] = Flag($"Leg {n} daily average");
            properties[L("observationShift")] = Whole($"Leg {n} observation shift (days)");
            properties[L("lookback")] = Whole($"Leg {n} lookback (days)");
            properties[L("paymentDelay")] = Whole($"Leg {n} payment delay (days)");
            properties[L("inArrears")] = Flag($"Leg {n} in arrears");
            properties[L("gearing")] = Number($"Leg {n} gearing");
            properties[L("currentReset")] = Number($"Leg {n} current reset (%)");
        }
        var required = new JsonArray(LineFields.Product, LineFields.End, LineFields.Direction);
        foreach (var n in new[] { 1, 2 })
            foreach (var f in new[] { "currency", "index", "notional", "paymentFrequency" }) required.Add(LineFields.Leg(n, f));
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }
}

/// <summary>
/// <c>rates.line.create</c> (ADR-0005): one line from its terms, in a workspace of the caller's, or a new one at the
/// valuation date given. A command: it edits the workspace through the page's own service, for the caller the server
/// names, and the edit log records where it came from.
/// </summary>
public sealed class RatesLineCreate(Func<RatesWorkspaces> workspaces) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.line.create", RatesContracts.Version, "Create a rates line",
        [new("terms", RatesCommandContracts.LineTerms, RatesContracts.Version, Cardinality.One)],
        [new("line", RatesCommandContracts.LineCreated, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = RatesCommand.CallerOf(invocation);
        var terms = (JsonObject)invocation.Inputs["terms"]!.DeepClone();
        var workspaceId = RatesCommand.TakeText(terms, "workspaceId");
        var valuationDate = RatesCommand.TakeText(terms, "valuationDate");
        if (workspaceId is null == valuationDate is null)
            throw new CapabilityFailure("ValidationFailed", "Name a workspace, or a valuation date for a new one.",
                [new("WorkspaceOrValuationDate", "/inputs/terms/workspaceId",
                    workspaceId is null ? "Name a workspace, or give the valuation date for a new one." : "A line takes its workspace's valuation date; give one or the other.")]);
        var service = workspaces();
        return RatesCommand.Refusing(() =>
        {
            // A new workspace is created only once the terms would make a priceable line in it, so a refused command
            // leaves nothing behind.
            var target = workspaceId;
            if (target is null)
            {
                var date = DateOnly.ParseExact(valuationDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                service.LineFrom(terms, date);
                target = service.Create(caller.UserId, date, caller.Origin).WorkspaceId;
            }
            var (state, line) = service.CreateLine(target, caller.UserId, terms, caller.Origin);
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
            {
                ["line"] = new JsonObject
                {
                    ["workspaceId"] = state.WorkspaceId, ["line"] = line, ["revision"] = state.Revision,
                    ["valuationDate"] = RatesEditRules.ValuationDate(state).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                },
            });
        });
    }
}

/// <summary><c>rates.live.start</c> (ADR-0005): starts ticking a workspace of the caller's, or joins its running subscription.</summary>
public sealed class RatesLiveStart(Func<RatesWorkspaces> workspaces) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.live.start", RatesContracts.Version, "Start ticking a rates workspace",
        [new("workspace", RatesCommandContracts.LiveTarget, RatesContracts.Version, Cardinality.One)],
        [new("subscription", RatesCommandContracts.LiveSubscription, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.Subscription, [CapabilityEffect.Subscribe]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = RatesCommand.CallerOf(invocation);
        var workspaceId = invocation.Inputs["workspace"]!["workspaceId"]!.GetValue<string>();
        return RatesCommand.Refusing(() => RatesCommand.Subscription(workspaceId, workspaces().StartLive(workspaceId, caller.UserId, caller.Origin)));
    }
}

/// <summary><c>rates.live.stop</c> (ADR-0005): stops a workspace's running subscription, for every page watching it.</summary>
public sealed class RatesLiveStop(Func<RatesWorkspaces> workspaces) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.live.stop", RatesContracts.Version, "Stop ticking a rates workspace",
        [new("workspace", RatesCommandContracts.LiveTarget, RatesContracts.Version, Cardinality.One)],
        [new("subscription", RatesCommandContracts.LiveSubscription, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Subscribe]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = RatesCommand.CallerOf(invocation);
        var workspaceId = invocation.Inputs["workspace"]!["workspaceId"]!.GetValue<string>();
        return RatesCommand.Refusing(() => RatesCommand.Subscription(workspaceId, workspaces().StopLiveOn(workspaceId, caller.UserId, caller.Origin)));
    }
}

static class RatesCommand
{
    public static Caller CallerOf(CapabilityInvocation invocation) =>
        invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");

    public static string? TakeText(JsonObject terms, string name)
    {
        var value = terms[name]?.GetValue<string>();
        terms.Remove(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static Task<IReadOnlyDictionary<string, JsonNode?>> Subscription(string workspaceId, string subscriptionId) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["subscription"] = new JsonObject { ["workspaceId"] = workspaceId, ["subscriptionId"] = subscriptionId },
        });

    /// <summary>The workspace service's refusals, as the command's: by code, with every reason a line's terms failed on.</summary>
    public static Task<IReadOnlyDictionary<string, JsonNode?>> Refusing(Func<Task<IReadOnlyDictionary<string, JsonNode?>>> act)
    {
        try
        {
            return act();
        }
        catch (LineTermsRefused refused)
        {
            throw new CapabilityFailure("InvalidTerms", refused.Message, [.. refused.Issues.Select(i => new CompositionIssue("InvalidTerms", "/inputs/terms", i))]);
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
        catch (KeyNotFoundException missing)
        {
            throw new CapabilityFailure("UnknownSubscription", missing.Message);
        }
    }
}

/// <summary>
/// The Rates commands and their contracts (ADR-0005). Registered apart from <see cref="RatesModule"/> because they act
/// through the workspace service, which is built from the registry; they reach it only when invoked.
/// </summary>
public sealed class RatesCommandsModule(Func<RatesWorkspaces> workspaces) : IWorkbenchModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new RatesLineCreate(workspaces));
        builder.AddCapability(new RatesLiveStart(workspaces));
        builder.AddCapability(new RatesLiveStop(workspaces));
        builder.AddContract(new ContractDefinition(RatesCommandContracts.LineTerms, RatesContracts.Version, "Rates line terms", RatesCommandContracts.LineTermsSchema()));
        builder.AddContract(new ContractDefinition(RatesCommandContracts.LineCreated, RatesContracts.Version, "Rates line created", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["line"] = new JsonObject { ["type"] = "string", ["title"] = "Line" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
                ["valuationDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Valuation date" },
            },
            ["required"] = new JsonArray("workspaceId", "line", "revision", "valuationDate"),
            ["additionalProperties"] = false,
        }));
        builder.AddContract(new ContractDefinition(RatesCommandContracts.LiveTarget, RatesContracts.Version, "Rates workspace to tick", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["workspaceId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace" } },
            ["required"] = new JsonArray("workspaceId"),
            ["additionalProperties"] = false,
        }));
        builder.AddContract(new ContractDefinition(RatesCommandContracts.LiveSubscription, RatesContracts.Version, "Rates live subscription", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["subscriptionId"] = new JsonObject { ["type"] = "string", ["title"] = "Live subscription" },
            },
            ["required"] = new JsonArray("workspaceId", "subscriptionId"),
            ["additionalProperties"] = false,
        }));
    }
}
