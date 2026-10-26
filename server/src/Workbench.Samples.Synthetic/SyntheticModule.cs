using System.Reflection;
using System.Text.Json;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Synthetic;

/// <summary>
/// Registers the synthetic stations sample: four capabilities in code, and its workflows, views, actions
/// and contracts as declarative JSON, the same documents an author or an agent would submit.
/// </summary>
public sealed class SyntheticModule : IWorkbenchModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new ListStations())
            .AddCapability(new GetStation())
            .AddCapability(new GenerateReadings())
            .AddCapability(new SummariseReadings());

        foreach (var contract in Read<List<ContractDefinition>>("contracts.json")) builder.AddContract(contract);
        foreach (var action in Read<List<ActionDefinition>>("actions.json")) builder.AddAction(action);
        foreach (var name in Names("workflow.")) builder.AddWorkflow(Read<WorkflowDefinition>(name));
        foreach (var name in Names("view.")) builder.AddView(Read<ViewDefinition>(name));
    }

    static readonly Assembly Assembly = typeof(SyntheticModule).Assembly;
    const string Prefix = "Workbench.Samples.Synthetic.Definitions.";

    static IEnumerable<string> Names(string kind) => Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix + kind, StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..]);

    static T Read<T>(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"Embedded definition {name} is missing.");
        return JsonSerializer.Deserialize<T>(stream, WireJson.Options)
            ?? throw new InvalidOperationException($"Embedded definition {name} is null.");
    }
}
