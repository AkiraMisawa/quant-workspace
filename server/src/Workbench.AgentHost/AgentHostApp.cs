using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.AgentHost.Contracts;

namespace Workbench.AgentHost;

/// <summary>
/// The agent host's web surface (ADR-0007 §5, C44): it answers turns for the workbench on the loopback interface only,
/// and only to a caller holding the key the two processes share. It holds the agent's model credential and nothing
/// that can act in the workbench; a tool call is a line on the turn's answer, executed by the workbench.
/// </summary>
public static class AgentHostApp
{
    /// <summary>Checks the configuration and maps the routes. The backend is registered by the caller.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var key = builder.Configuration["AgentHost:Key"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("AgentHost:Key is not set, or is shorter than 32 characters. The workbench sends the same key as Agent:Copilot:HostKey.");
        builder.Services.AddSingleton<HostTurns>();
        var app = builder.Build();
        var expected = Encoding.UTF8.GetBytes(key);

        app.Use(async (context, next) =>
        {
            // The host answers this machine only: the model credential is the agent's, and nothing else may spend it.
            if (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var given = Encoding.UTF8.GetBytes(context.Request.Headers[AgentHostWire.KeyHeader].ToString());
            if (!CryptographicOperations.FixedTimeEquals(given, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });

        app.MapPost(AgentHostWire.Turns, async (HttpContext context, HostTurns turns) =>
        {
            var turn = await context.Request.ReadFromJsonAsync<HostTurn>(AgentHostWire.Json, context.RequestAborted);
            if (turn is null || string.IsNullOrWhiteSpace(turn.ThreadId) || string.IsNullOrWhiteSpace(turn.RunId)
                || string.IsNullOrWhiteSpace(turn.Model) || string.IsNullOrWhiteSpace(turn.Prompt) || turn.Tools is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("A turn names its thread, run, model and prompt, and lists its tools.");
                return;
            }
            if (turns.Start(turn, context.RequestAborted) is not { } running)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsync($"Run {turn.RunId} is already being answered.");
                return;
            }
            context.Response.ContentType = "application/x-ndjson";
            // The headers go at once, so the workbench knows the turn was taken before the model's first word.
            await context.Response.StartAsync(CancellationToken.None);
            await foreach (var line in running.Lines.Reader.ReadAllAsync(CancellationToken.None))
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(line, AgentHostWire.Json) + "\n", CancellationToken.None);
                await context.Response.Body.FlushAsync(CancellationToken.None);
            }
        });

        app.MapPost("/turns/{runId}/calls/{callId}", async (string runId, string callId, HostToolResult result, HostTurns turns) =>
            turns.Answer(runId, callId, result.Content) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/turns/{runId}/abort", (string runId, HostTurns turns) =>
            turns.Abort(runId) ? Results.NoContent() : Results.NotFound());

        // Answers once the host is up and the caller holds the key; it reaches no model, so it spends nothing.
        app.MapGet("/health", () => Results.Ok("ok"));

        app.MapGet("/models", async (IModelBackend backend, CancellationToken cancellationToken) =>
            Results.Ok(await backend.ModelsAsync(cancellationToken)));

        return app;
    }
}
