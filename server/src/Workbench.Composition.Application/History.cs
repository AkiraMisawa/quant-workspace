using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>
/// A domain's view of the pricing history (docs/05 §7), registered by its module. It names the result
/// contracts that make a Run its own and turns one Run into one row of its figures. A figure that is not
/// available, because the Run failed, is still running or its result expired, is left empty, never zero.
/// </summary>
public interface IHistoryProjection
{
    /// <summary>The domain's key, such as <c>xva</c>.</summary>
    string Domain { get; }

    string Title { get; }

    /// <summary>A Run whose workflow declares any of these contracts for its outputs belongs to this domain.</summary>
    IReadOnlyList<string> ResultContracts { get; }

    /// <summary>The domain's columns, after the history's own.</summary>
    IReadOnlyList<DatasetColumn> Columns { get; }

    /// <summary>The domain's figures for one Run, keyed by column. Missing keys are empty cells.</summary>
    JsonObject Project(RunRecord run, IDataReader data);
}

/// <summary>A domain the history can be viewed by.</summary>
public sealed record HistoryDomain(string Domain, string Title, IReadOnlyList<DatasetColumn> Columns);

/// <summary>A page of history rows, in ExGrid's fetching-source shape, with the columns in force.</summary>
public sealed record HistoryPage(IReadOnlyList<DatasetColumn> Columns, IReadOnlyList<JsonObject> Rows, int Start, int TotalCount, string OrderToken);

/// <summary>
/// The Pricing DB (docs/05 §7): every Run whatever started it, newest first, with one domain's figures when a
/// domain is active. The grid shows it; the server filters and pages it.
/// </summary>
public sealed class HistoryService(CompositionRegistry registry, RunService runs, IDataStore data)
{
    /// <summary>The history's own columns, shown under every domain.</summary>
    public static readonly IReadOnlyList<DatasetColumn> Columns =
    [
        new("runId", DatasetColumnType.String, false),
        new("acceptedAt", DatasetColumnType.String, false),
        new("status", DatasetColumnType.String, false),
        new("origin", DatasetColumnType.String, false),
        new("owner", DatasetColumnType.String, false),
        new("workspaceId", DatasetColumnType.String, true),
        new("workspaceRevision", DatasetColumnType.Integer, true),
        new("analysisGroupId", DatasetColumnType.String, true),
        new("parentRunId", DatasetColumnType.String, true),
        new("conversationId", DatasetColumnType.String, true),
    ];

    public IReadOnlyList<HistoryDomain> Domains() =>
        [.. registry.HistoryProjections.Select(p => new HistoryDomain(p.Domain, p.Title, [.. Columns, .. p.Columns]))];

    /// <param name="owner">Whose Runs; null for every owner (a developer's or an administrator's view, C31).</param>
    /// <param name="domain">The active domain, or null for every Run with the history's columns only.</param>
    public HistoryPage Page(string? owner, string? domain, int start, int count)
    {
        var projection = domain is null ? null
            : registry.HistoryProjections.SingleOrDefault(p => p.Domain == domain) ?? throw new HistoryRefused("UnknownDomain", $"No history view for {domain}.");
        var (page, total) = runs.List(new RunQuery(owner, start, count, ResultContracts: projection?.ResultContracts));
        var rows = page.Select(run =>
        {
            var row = new JsonObject
            {
                ["runId"] = run.RunId,
                ["acceptedAt"] = run.AcceptedAt.ToString("O"),
                ["status"] = JsonSerializer.SerializeToNode(run.Status, WireJson.Options),
                ["origin"] = JsonSerializer.SerializeToNode(run.Origin, WireJson.Options),
                ["owner"] = run.Owner,
                ["workspaceId"] = run.Relations.WorkspaceId,
                ["workspaceRevision"] = run.Relations.WorkspaceRevision,
                ["analysisGroupId"] = run.Relations.AnalysisGroupId,
                ["parentRunId"] = run.Relations.ParentRunId,
                ["conversationId"] = run.Relations.ConversationId,
            };
            if (projection is not null)
                foreach (var (field, value) in projection.Project(run, data))
                {
                    if (row.ContainsKey(field)) throw new InvalidOperationException($"History view {projection.Domain} redefines column {field}.");
                    row[field] = value?.DeepClone();
                }
            return row;
        }).ToList();
        // Newest first: the order moves when a Run is added, which the total and the newest Run name together.
        var newest = start == 0 && page.Count > 0 ? page[0] : runs.List(new RunQuery(owner, 0, 1, ResultContracts: projection?.ResultContracts)).Runs.FirstOrDefault();
        var token = $"{total}/{newest?.RunId}";
        return new(projection is null ? Columns : [.. Columns, .. projection.Columns], rows, start, total, token);
    }
}

/// <summary>A history request refused. Nothing was read.</summary>
public sealed class HistoryRefused(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
