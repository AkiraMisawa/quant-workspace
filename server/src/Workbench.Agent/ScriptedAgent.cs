using System.Text.Json.Nodes;
using Workbench.Composition.Application;

namespace Workbench.Agent;

/// <summary>
/// A request the scripted agent understands, registered by the module whose command it calls (ADR-0007 §6). It finds
/// itself in the user's words, builds the command's inputs from only what the words and the context state, and says
/// what was done. The kernel's scripted agent knows no domain; each module brings its own.
/// </summary>
public interface IScriptedRequest
{
    /// <summary>The command capability this request calls.</summary>
    string CapabilityId { get; }

    /// <summary>What a user may write to reach it, for the agent's answer when nothing matched.</summary>
    string Example { get; }

    /// <summary>How to supply what a refusal says is missing, added to the agent's question.</summary>
    string Hint { get; }

    /// <summary>Where in the request it is asked for (lower case), or null when it is not.</summary>
    int? FindIn(string request);

    /// <summary>The command's inputs, by port. Only terms the request or the context states; nothing is defaulted.</summary>
    JsonObject Inputs(ScriptedStep step);

    /// <summary>The sentence that reports a done call.</summary>
    string Done(JsonObject outputs);
}

/// <summary>
/// What a scripted request is built from: the request in lower case, the context the pane sent, and the outputs of
/// the calls already done for this request, in order (so "make a swap and start ticking" ticks the swap's workspace).
/// </summary>
public sealed record ScriptedStep(string Request, IReadOnlyList<AgentContext> Context, IReadOnlyList<(string CapabilityId, JsonObject Outputs)> Earlier);

/// <summary>
/// The stand-in runtime (ADR-0007 §6): no model, and labelled so. It reads the user's latest request, or that request
/// joined to the one before when that one was refused and the latest does not ask for the refused thing afresh (the
/// user is answering its question, perhaps adding to it: "pay fixed, and start ticking"); calls each registered request it
/// finds, in the order written; stops at the first refusal and asks for what it names; and reports what was done.
/// Every decision is a function of the conversation, so a transcript is repeatable.
/// </summary>
public sealed class ScriptedAgent(IEnumerable<IScriptedRequest> requests) : IAgentRuntime
{
    readonly IReadOnlyList<IScriptedRequest> requests = [.. requests];

    public AgentRuntimeInfo Info { get; } = new("scripted", "scripted · no model", Simulated: true);

    public async Task RunAsync(AgentTurn turn, IAgentTurnSink sink, CancellationToken cancellationToken)
    {
        var messages = turn.Messages.ToList();
        while (true)
        {
            var (text, call) = Decide(messages, turn);
            if (call is null)
            {
                await sink.SayAsync(text!, cancellationToken);
                return;
            }
            var result = await sink.CallAsync(call, cancellationToken);
            messages.Add(new AgentMessage($"local-{call.Id}", "assistant", null, [call]));
            messages.Add(AgentMessage.Tool($"local-result-{call.Id}", call.Id, result.Text));
        }
    }

    /// <summary>The next step: words to end the run with, or one tool call.</summary>
    (string? Text, AgentToolCall? Call) Decide(IReadOnlyList<AgentMessage> conversation, AgentTurn turn)
    {
        var turns = Turns(conversation);
        if (turns.Count == 0) return Reply("There is no request to answer.");
        var current = turns[^1];

        // The request: the latest words, joined to the turns before while the one before was refused and the words do
        // not ask for the refused request afresh. Calls done in those turns are not made again.
        var joined = new List<Turn> { current };
        while (turns.IndexOf(joined[0]) is var at and > 0 && turns[at - 1].Refused && !Restates(joined[0].Request, turns[at - 1].Results[^1].CapabilityId))
            joined.Insert(0, turns[at - 1]);
        var request = string.Join(" ", joined.Select(t => t.Request));
        var plan = Matches(request);
        if (plan.Count == 0)
            return Reply("I am the scripted stand-in for the agent and have no model, so I understand only requests like these: "
                + string.Join("; ", requests.Select(r => $"\"{r.Example}\"")) + ".");

        var earlier = joined.Take(joined.Count - 1).SelectMany(t => t.Results).Where(r => r.Succeeded).ToList();
        var now = current.Results;
        if (now.LastOrDefault() is { Succeeded: false } refused)
            return Reply(Refusal(refused, plan));

        var done = earlier.Concat(now).Select(r => (r.CapabilityId, r.Outputs)).ToList();
        var next = plan.FirstOrDefault(p => done.All(d => d.CapabilityId != p.CapabilityId));
        if (next is not null)
        {
            var name = AgentToolGateway.NameOf(next.CapabilityId);
            if (turn.Tools.All(t => t.Name != name))
                return Reply($"{next.CapabilityId} is not offered to me, so I cannot do that.");
            var inputs = next.Inputs(new(request, turn.Context, done));
            return (null, new($"call-{Guid.CreateVersion7():N}", name, inputs.ToJsonString()));
        }
        if (now.Count == 0) return Reply("That was done already; there is nothing more to do for it.");
        return Reply(string.Join(" ", now.Select(r => plan.First(p => p.CapabilityId == r.CapabilityId).Done(r.Outputs))));
    }

    bool Restates(string request, string capabilityId) => requests.Any(r => r.CapabilityId == capabilityId && r.FindIn(request) is not null);

    List<IScriptedRequest> Matches(string request) =>
        [.. requests.Select(r => (Request: r, At: r.FindIn(request))).Where(m => m.At is not null).OrderBy(m => m.At).Select(m => m.Request)];

    static string Refusal(ToolResult refused, IReadOnlyList<IScriptedRequest> plan)
    {
        var issues = refused.Issues.ToList();
        var missing = issues.Where(i => i.Code == "Required").Select(i => i.Path[(i.Path.LastIndexOf('/') + 1)..]).ToList();
        var other = issues.Where(i => i.Code != "Required").Select(i => i.Message).ToList();
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"it needs {string.Join(", ", missing)}");
        parts.AddRange(other);
        var hint = plan.FirstOrDefault(p => p.CapabilityId == refused.CapabilityId)?.Hint;
        return $"{refused.CapabilityId} was refused: {string.Join("; ", parts)}. Nothing was changed by it. Tell me what to use"
            + (string.IsNullOrEmpty(hint) ? "." : $" ({hint}).");
    }

    static (string? Text, AgentToolCall? Call) Reply(string text) => (text, null);

    /// <summary>A user message and the tool results that followed it.</summary>
    sealed record Turn(string Request, List<ToolResult> Results)
    {
        public bool Refused => Results.LastOrDefault() is { Succeeded: false };
    }

    static List<Turn> Turns(IReadOnlyList<AgentMessage> messages)
    {
        var turns = new List<Turn>();
        foreach (var message in messages)
        {
            if (message.Role == "user") turns.Add(new(message.Content!.ToLowerInvariant(), []));
            else if (message.Role == "tool" && turns.Count > 0) turns[^1].Results.Add(ToolResult.Parse(message.Content!));
        }
        return turns;
    }
}
