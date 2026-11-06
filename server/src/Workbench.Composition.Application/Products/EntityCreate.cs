using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application.Products;

/// <summary>
/// <c>entity.create</c> (ADR-0008 §1, §5): one entity of a described product in a workspace of the caller's, from the
/// terms as a person would type them after <c>new &lt;product&gt;</c>. A command (ADR-0005): it acts for the caller the
/// server names, through the same service the page uses, and refuses every missing or unreadable term by name.
/// </summary>
public sealed class EntityCreate(Func<ProductService> products) : ICapabilityHandler
{
    public const string Request = "entity.create-request";
    public const string Created = "entity.created";
    public const string Version = "1.0.0";

    public CapabilityDescriptor Descriptor { get; } = new("entity.create", Version, "Create an entity of a described product",
        [new("request", Request, Version, Cardinality.One)],
        [new("entity", Created, Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"]!;
        var product = request["product"]!.GetValue<string>();
        var workspaceId = request["workspaceId"]!.GetValue<string>();
        var text = request["terms"]?.GetValue<string>() ?? "";
        try
        {
            var (state, entity, reading) = products().Create(workspaceId, null, caller.UserId, product, text, caller.Origin);
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
            {
                ["entity"] = new JsonObject
                {
                    ["workspaceId"] = state.WorkspaceId, ["entity"] = entity, ["revision"] = state.Revision, ["product"] = product,
                    // Flat, as every contract is: "field from source, …", as the edit log says it.
                    ["defaulted"] = string.Join(", ", reading.Sources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} from {p.Value}")),
                },
            });
        }
        catch (ProductTermsRefused refused)
        {
            throw new CapabilityFailure("InvalidTerms", refused.Message,
                [.. refused.Issues.Select(i => new CompositionIssue(i.Code, i.Field is null ? "/inputs/request/terms" : $"/inputs/request/terms/{i.Field}", i.Message))]);
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }
}

/// <summary>The kernel's own command for described products, and its contracts. Knows no product.</summary>
public sealed class ProductsModule(Func<ProductService> products) : IWorkbenchModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new EntityCreate(products));
        builder.AddContract(new ContractDefinition(EntityCreate.Request, EntityCreate.Version, "Create an entity", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["product"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Product, such as rates.irs" },
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace" },
                ["terms"] = new JsonObject { ["type"] = "string", ["title"] = "Terms as typed after new <product>, such as 10y 1000 pay" },
            },
            ["required"] = new JsonArray("product", "workspaceId"),
            ["additionalProperties"] = false,
        }));
        builder.AddContract(new ContractDefinition(EntityCreate.Created, EntityCreate.Version, "Entity created", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["entity"] = new JsonObject { ["type"] = "string", ["title"] = "Entity" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
                ["product"] = new JsonObject { ["type"] = "string", ["title"] = "Product" },
                ["defaulted"] = new JsonObject { ["type"] = "string", ["title"] = "Fields filled by a default, and from where" },
            },
            ["required"] = new JsonArray("workspaceId", "entity", "revision", "product", "defaulted"),
            ["additionalProperties"] = false,
        }));
    }
}
