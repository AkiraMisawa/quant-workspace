using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Workbench.Agent;

namespace Workbench.Web;

/// <summary>
/// The AI pane's endpoints (ADR-0007): AG-UI runs streamed as server-sent events, read again from any position, the
/// thread as the server holds it, and cancel. The caller is the signed-in user; nothing the client sends names them.
/// </summary>
public static class AgentApi
{
    public static void MapAgentApi(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/agent");

        agent.MapGet("/runtime", (AgentService service) => service.Runtime);

        agent.MapPost("/runs", (JsonObject input, AgentService service, CurrentUser user, CancellationToken cancellationToken) =>
            service.Start(input, user.Id) switch
            {
                AgentStart.Running running => Stream(running.Run, 0, cancellationToken),
                AgentStart.Refused { Code: "RunInProgress" } refused => Refuse(refused, 409),
                AgentStart.Refused { Code: "UnknownThread" } refused => Refuse(refused, 404),
                AgentStart.Refused refused => Refuse(refused, 422),
                _ => throw new InvalidOperationException("Unhandled start."),
            });

        // A reader that lost the stream reads the rest from the position it reached (AI04); the run was never stopped.
        agent.MapGet("/threads/{threadId}/runs/{runId}/events", (string threadId, string runId, int? after, AgentThreads threads, CurrentUser user,
            CancellationToken cancellationToken) =>
            RunOf(threads, user, threadId, runId) is { } run ? Stream(run, after ?? 0, cancellationToken) : UnknownRun(threadId, runId));

        agent.MapGet("/threads/{threadId}", (string threadId, AgentThreads threads, CurrentUser user) =>
            threads.Find(threadId) is { } thread && thread.Owner == user.Id
                ? Results.Ok(new
                {
                    threadId = thread.ThreadId,
                    messages = thread.Messages.Select(AgUiEvents.Message),
                    activeRunId = thread.ActiveRun?.RunId,
                })
                : Results.Json(new Api.ErrorEnvelope("UnknownThread", $"Thread {threadId} does not exist.", []), statusCode: 404));

        agent.MapPost("/threads/{threadId}/runs/{runId}/cancel", (string threadId, string runId, AgentThreads threads, CurrentUser user) =>
        {
            if (RunOf(threads, user, threadId, runId) is not { } run) return UnknownRun(threadId, runId);
            run.Cancel();
            return Results.Accepted();
        });
    }

    static AgentRun? RunOf(AgentThreads threads, CurrentUser user, string threadId, string runId) =>
        threads.Find(threadId) is { } thread && thread.Owner == user.Id ? thread.Run(runId) : null;

    static IResult Stream(AgentRun run, int after, CancellationToken cancellationToken) =>
        TypedResults.ServerSentEvents(Events(run, after, cancellationToken));

    static async IAsyncEnumerable<SseItem<string>> Events(AgentRun run, int after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var (index, e) in run.ReadAsync(after, cancellationToken))
            yield return new SseItem<string>(e.ToJsonString()) { EventId = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    static IResult Refuse(AgentStart.Refused refused, int status) =>
        Results.Json(new Api.ErrorEnvelope(refused.Code, refused.Message, []), statusCode: status);

    static IResult UnknownRun(string threadId, string runId) =>
        Results.Json(new Api.ErrorEnvelope("UnknownRun", $"Run {runId} of thread {threadId} does not exist.", []), statusCode: 404);
}
