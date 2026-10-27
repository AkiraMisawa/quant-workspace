using System.Collections.Concurrent;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Workbench.AgentHost.Contracts;

namespace Workbench.AgentHost;

/// <summary>
/// Where the Copilot runtime keeps its state (under the home of the user the host runs as, unless configured), and the
/// empty directory its sessions are started in.
/// </summary>
public sealed record CopilotBackendOptions(string BaseDirectory, string WorkingDirectory)
{
    /// <summary>The environment variable the agent's credential is read from (C45).</summary>
    public const string TokenVariable = "COPILOT_GITHUB_TOKEN";

    public static CopilotBackendOptions From(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["AgentHost:Copilot:BaseDirectory"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "quant-workbench", "agent-host");
        var baseDirectory = Path.GetFullPath(configured, environment.ContentRootPath);
        return new(baseDirectory, Path.Combine(baseDirectory, "empty"));
    }

    /// <summary>
    /// Takes the credential from a file only the agent's user can read (AgentHost:Copilot:TokenFile), so it never
    /// appears on a command line, and hands it to the SDK's runtime the way the SDK reads it. Without the setting, the
    /// variable must already be set for this process.
    /// </summary>
    public static void LoadToken(IConfiguration configuration)
    {
        if (configuration["AgentHost:Copilot:TokenFile"] is not { Length: > 0 } file) return;
        var token = File.ReadAllText(file).Trim();
        if (token.Length == 0) throw new InvalidOperationException($"AgentHost:Copilot:TokenFile {file} is empty.");
        Environment.SetEnvironmentVariable(TokenVariable, token);
    }
}

/// <summary>
/// The Copilot SDK behind the agent host (ADR-0007 §5, C44). The client is in the SDK's empty mode, which exposes no
/// built-in tool: no shell, no file system, no web. Each session is offered the workbench's tools and nothing else, a
/// permission request for anything else is rejected, and a call to one of those tools is handed to the workbench,
/// which executes it for the signed-in user. The credential is the agent's own, read from COPILOT_GITHUB_TOKEN and given
/// to the SDK; the logged-in user's credentials are never used, so a missing token fails instead of borrowing one.
/// One session answers one workbench thread, and keeps its conversation.
/// </summary>
public sealed class CopilotBackend(CopilotBackendOptions options, ILogger<CopilotBackend> logger) : IModelBackend, IAsyncDisposable
{
    readonly SemaphoreSlim starting = new(1, 1);
    readonly ConcurrentDictionary<string, Thread> threads = new(StringComparer.Ordinal);
    CopilotClient? client;

    public async Task<IReadOnlyList<string>> ModelsAsync(CancellationToken cancellationToken)
    {
        var models = await (await ClientAsync(cancellationToken)).ListModelsAsync(cancellationToken);
        return [.. models.Select(m => m.Id)];
    }

    public async Task RunAsync(HostTurn turn, IModelTurnSink sink, CancellationToken cancellationToken)
    {
        var thread = threads.GetOrAdd(turn.ThreadId, _ => new Thread());
        await thread.Gate.WaitAsync(cancellationToken);
        try
        {
            var session = await SessionAsync(thread, turn, cancellationToken);
            var fresh = !thread.Prompted;
            thread.Prompted = true;
            thread.Current = sink;
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = session.On<SessionEvent>(e =>
            {
                switch (e)
                {
                    // A sub-agent's words are not the reply; only the root agent speaks to the user.
                    case AssistantMessageEvent message when message.AgentId is null && !string.IsNullOrWhiteSpace(message.Data.Content):
                        sink.Say(message.Data.Content);
                        break;
                    case SessionErrorEvent error:
                        idle.TrySetException(new ModelFailed(error.Data.Message ?? error.Data.ErrorType ?? "Copilot reported an error without a message."));
                        break;
                    case SessionIdleEvent:
                        idle.TrySetResult();
                        break;
                }
            });
            using var abort = cancellationToken.Register(() => _ = AbortAsync(session));
            await session.SendAsync(new MessageOptions { Prompt = Prompt(turn, fresh) }, cancellationToken);
            await idle.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            thread.Current = null;
            thread.Gate.Release();
        }
    }

    /// <summary>
    /// The thread's session, made on its first turn. A later turn that names another model switches it; one that offers
    /// other tools (the workbench restarted with other commands) starts a new session, told the conversation again.
    /// </summary>
    async Task<CopilotSession> SessionAsync(Thread thread, HostTurn turn, CancellationToken cancellationToken)
    {
        var toolsKey = JsonSerializer.Serialize(turn.Tools, AgentHostWire.Json);
        if (thread.Session is { } stale && thread.ToolsKey != toolsKey)
        {
            await stale.DisposeAsync();
            thread.Session = null;
            thread.Prompted = false;
        }
        if (thread.Session is { } session)
        {
            if (thread.Model != turn.Model)
            {
                await session.SetModelAsync(turn.Model, cancellationToken);
                thread.Model = turn.Model;
            }
            return session;
        }
        var available = new ToolSet();
        foreach (var tool in turn.Tools) available.AddCustom(tool.Name);
        Directory.CreateDirectory(options.WorkingDirectory);
        thread.Session = await (await ClientAsync(cancellationToken)).CreateSessionAsync(new SessionConfig
        {
            Model = turn.Model,
            Tools = Tools(turn, thread),
            AvailableTools = available,
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = turn.Instructions },
            OnPermissionRequest = (request, _) =>
            {
                logger.LogWarning("Rejected a permission request of kind {Kind}; the agent may call the workbench's tools only.", request.Kind);
                // The SDK marks its decision type as evaluation-only (GHCP001); rejecting is the only decision taken here.
#pragma warning disable GHCP001
                return Task.FromResult(PermissionDecision.Reject("This agent may call the workbench's tools only."));
#pragma warning restore GHCP001
            },
            WorkingDirectory = options.WorkingDirectory,
            EnableConfigDiscovery = false,
            SkipCustomInstructions = true,
            EnableSkills = false,
            EnableSessionStore = false,
            Streaming = false,
        }, cancellationToken);
        thread.Model = turn.Model;
        thread.ToolsKey = toolsKey;
        return thread.Session;
    }

    static List<AIFunctionDeclaration> Tools(HostTurn turn, Thread thread) => [.. turn.Tools.Select(t => (AIFunctionDeclaration)new WorkbenchTool(t, thread))];

    /// <summary>
    /// The text the model is sent. A session made for a thread that already has a conversation (after either process
    /// restarted) is told that conversation first, so it is not answered as a new one.
    /// </summary>
    static string Prompt(HostTurn turn, bool fresh) => fresh && !string.IsNullOrWhiteSpace(turn.Earlier)
        ? $"Earlier in this conversation:\n{turn.Earlier}\n\nThe user now asks:\n{turn.Prompt}"
        : turn.Prompt;

    async Task AbortAsync(CopilotSession session)
    {
        try
        {
            await session.AbortAsync(CancellationToken.None);
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Aborting a Copilot turn failed.");
        }
    }

    async Task<CopilotClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (client is { } started) return started;
        await starting.WaitAsync(cancellationToken);
        try
        {
            if (client is { } raced) return raced;
            var token = Environment.GetEnvironmentVariable(CopilotBackendOptions.TokenVariable);
            if (string.IsNullOrWhiteSpace(token))
                throw new ModelFailed($"{CopilotBackendOptions.TokenVariable} is not set for the agent host; the agent has no credential of its own.");
            Directory.CreateDirectory(options.BaseDirectory);
            var created = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = options.BaseDirectory,
                WorkingDirectory = options.WorkingDirectory,
                // Given explicitly: with the logged-in user turned off, the runtime does not look for the variable itself.
                GitHubToken = token,
                UseLoggedInUser = false,
            });
            await created.StartAsync(cancellationToken);
            client = created;
            return created;
        }
        finally
        {
            starting.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var thread in threads.Values)
            if (thread.Session is { } session) await session.DisposeAsync();
        if (client is { } started) await started.DisposeAsync();
    }

    sealed class Thread
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CopilotSession? Session { get; set; }
        public string? Model { get; set; }
        public string? ToolsKey { get; set; }
        public bool Prompted { get; set; }
        public IModelTurnSink? Current { get; set; }
    }

    /// <summary>
    /// One workbench tool as the SDK sees it: its schema as the workbench wrote it, no permission prompt (the workbench
    /// decides what the user may do), and a call that is handed to the turn answering now.
    /// </summary>
    sealed class WorkbenchTool(HostTool tool, Thread thread) : AIFunction
    {
        static readonly IReadOnlyDictionary<string, object?> SkipPermission = new Dictionary<string, object?> { ["skip_permission"] = true };
        readonly JsonElement schema = JsonSerializer.SerializeToElement(tool.Parameters, AgentHostWire.Json);

        public override string Name => tool.Name;
        public override string Description => tool.Description;
        public override JsonElement JsonSchema => schema;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => SkipPermission;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            if (arguments.Context?.TryGetValue(typeof(ToolInvocation), out var found) != true || found is not ToolInvocation invocation)
                throw new InvalidOperationException($"Copilot called {tool.Name} without naming the call.");
            var sink = thread.Current ?? throw new InvalidOperationException($"Copilot called {tool.Name} when no turn was being answered.");
            var written = invocation.Arguments is { } given ? given.GetRawText() : "{}";
            return await sink.CallAsync(invocation.ToolCallId, invocation.ToolName, written, cancellationToken);
        }
    }
}
