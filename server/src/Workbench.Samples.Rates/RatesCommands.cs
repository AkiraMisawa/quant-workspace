using System.Globalization;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Rates;

public static class RatesCommandContracts
{
    public const string NewWorkspace = "rates.workspace-new";
    public const string Workspace = "rates.workspace";
    public const string LiveTarget = "rates.live-target";
    public const string LiveSubscription = "rates.live-subscription";
}

/// <summary>
/// <c>rates.workspace.create</c> (ADR-0005, ADR-0008 §5): an empty Rates workspace of the caller's at a valuation date.
/// Lines go into it through the generic <c>entity.create</c>, with the same short terms the command line takes; this
/// command replaced <c>rates.line.create</c>, which took every leg field and made the workspace as it went.
/// </summary>
public sealed class RatesWorkspaceCreate(Func<RatesWorkspaces> workspaces) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.workspace.create", RatesContracts.Version, "Create a rates workspace",
        [new("request", RatesCommandContracts.NewWorkspace, RatesContracts.Version, Cardinality.One)],
        [new("workspace", RatesCommandContracts.Workspace, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = RatesCommand.CallerOf(invocation);
        var date = DateOnly.ParseExact(invocation.Inputs["request"]!["valuationDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var state = workspaces().Create(caller.UserId, date, caller.Origin);
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["workspace"] = new JsonObject
            {
                ["workspaceId"] = state.WorkspaceId, ["revision"] = state.Revision,
                ["valuationDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
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
        builder.AddCapability(new RatesWorkspaceCreate(workspaces));
        builder.AddCapability(new RatesLiveStart(workspaces));
        builder.AddCapability(new RatesLiveStop(workspaces));
        builder.AddContract(new ContractDefinition(RatesCommandContracts.NewWorkspace, RatesContracts.Version, "New rates workspace", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["valuationDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Valuation date" } },
            ["required"] = new JsonArray("valuationDate"),
            ["additionalProperties"] = false,
        }));
        builder.AddContract(new ContractDefinition(RatesCommandContracts.Workspace, RatesContracts.Version, "Rates workspace", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
                ["valuationDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Valuation date" },
            },
            ["required"] = new JsonArray("workspaceId", "revision", "valuationDate"),
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
