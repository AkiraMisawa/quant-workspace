using Workbench.AgentHost.Contracts;

namespace Workbench.AgentHost;

/// <summary>Where a backend answers one turn: what the model says, and the tool calls whose results the workbench sends back.</summary>
public interface IModelTurnSink
{
    void Say(string text);

    /// <summary>Hands the call to the workbench and waits for its result, the JSON text the model reads.</summary>
    Task<string> CallAsync(string id, string name, string arguments, CancellationToken cancellationToken);
}

/// <summary>The model behind the host. The Copilot SDK in use; a fake in tests. Returning ends the turn.</summary>
public interface IModelBackend
{
    Task RunAsync(HostTurn turn, IModelTurnSink sink, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ModelsAsync(CancellationToken cancellationToken);
}

/// <summary>A failure the model's side reported, said to the workbench as it was said.</summary>
public sealed class ModelFailed(string message) : Exception(message);
