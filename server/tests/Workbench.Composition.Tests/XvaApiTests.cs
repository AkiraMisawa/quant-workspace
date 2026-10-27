using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Workbench.Composition.Tests;

// The XVA workspace over HTTP: commands carry the revision they were taken against (ADR-0003, spine 6).
public class XvaApiTests(WorkbenchFactory factory) : IClassFixture<WorkbenchFactory>
{
    readonly HttpClient client = factory.CreateClient();

    async Task<JsonObject> Create(string registration = "ABC/LE-01", int size = 200)
    {
        var response = await client.PostAsJsonAsync("/api/xva/workspaces", new { registration, valueDate = "2026-10-26", portfolioSize = size });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    static string Id(JsonObject ws) => ws["workspaceId"]!.GetValue<string>();
    static long Revision(JsonObject ws) => ws["revision"]!.GetValue<long>();

    async Task<HttpResponseMessage> Post(JsonObject ws, string path, object body) =>
        await client.PostAsJsonAsync($"/api/xva/workspaces/{Id(ws)}/{path}", body);

    async Task<JsonObject> Page(JsonObject ws, int start, int count, params object[] sorts)
    {
        var response = await Post(ws, "rows", new { start, count, sorts });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact] // ADR-0003 §4: the server pages the effective rows for ExGrid's fetching source
    public async Task Rows_are_paged_and_sorted_on_the_server()
    {
        var ws = await Create();

        var page = await Page(ws, 10, 20, new { column = "notional", direction = "descending" });

        Assert.Equal(10, page["start"]!.GetValue<int>());
        Assert.Equal(20, page["rows"]!.AsArray().Count);
        var notionals = page["rows"]!.AsArray().Select(r => r!["notional"]!.GetValue<decimal>()).ToList();
        Assert.Equal(notionals.OrderDescending(), notionals);
        Assert.Equal(ws["trades"]!.GetValue<int>(), page["totalCount"]!.GetValue<int>());
    }

    [Fact] // Spine 1: a sort the server cannot honour is refused by name
    public async Task An_unknown_sort_column_is_refused()
    {
        var ws = await Create();

        var response = await Post(ws, "rows", new { start = 0, count = 10, sorts = new[] { new { column = "pv", direction = "ascending" } } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("UnknownColumn", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }

    [Fact] // Spine 6: an edit taken against an older revision is refused, whatever the timing
    public async Task An_edit_against_a_moved_revision_is_refused()
    {
        var ws = await Create();
        var edited = await Post(ws, "edits", new { revision = Revision(ws), entity = "trade:DRAFT-1", field = "csa", value = "CSA-B" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var stale = await Post(ws, "edits", new { revision = Revision(ws), entity = "trade:DRAFT-2", field = "csa", value = "CSA-B" });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("RevisionMoved", (await stale.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }

    [Fact] // ADR-0003 §6: changing counterparty drops the confirmed edits and keeps a new trade's own content
    public async Task A_counterparty_change_needs_the_confirmed_count_and_keeps_draft_content()
    {
        var ws = await Create();
        ws = (await (await Post(ws, "edits", new { revision = Revision(ws), entity = "trade:DRAFT-1", field = "csa", value = "CSA-B" })).Content.ReadFromJsonAsync<JsonObject>())!;
        ws = (await (await Post(ws, "edits", new { revision = Revision(ws), entity = "trade:DRAFT-1", field = "notional", value = 7_000_000 })).Content.ReadFromJsonAsync<JsonObject>())!;
        var preview = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{Id(ws)}/counterparty-change"))!;
        Assert.Equal(1, preview["count"]!.GetValue<int>());
        Assert.Equal(1, preview["byKind"]!["xva.assignment"]!.GetValue<int>());

        var stale = await Post(ws, "counterparty", new { revision = Revision(ws), registration = "ABC/LE-02", confirmedCount = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var moved = await Post(ws, "counterparty", new { revision = Revision(ws), registration = "ABC/LE-02", confirmedCount = 1 });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var result = (await moved.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("ABC/LE-02", result["workspace"]!["registration"]!["key"]!.GetValue<string>());
        Assert.Equal(1, result["carried"]!["carried"]!.GetValue<int>());
        var draft = (await Page(result["workspace"]!.AsObject(), 0, 2000))["rows"]!.AsArray().Single(r => r!["tradeId"]!.GetValue<string>() == "DRAFT-1")!;
        Assert.Equal((7_000_000m, "CSA-C"), (draft["notional"]!.GetValue<decimal>(), draft["csa"]!.GetValue<string>()));
    }

    [Fact] // ADR-0003 §6: Price freezes the revision and its Run computes simulated CVA and FVA
    public async Task Price_submits_one_run_for_the_revision()
    {
        var ws = await Create(size: 50);
        var key = Guid.NewGuid().ToString();

        var response = await Post(ws, "price", new { revision = Revision(ws), idempotencyKey = key });
        var again = await Post(ws, "price", new { revision = Revision(ws), idempotencyKey = key });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<string>();
        Assert.Equal(runId, (await again.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<string>());
        JsonObject run;
        do run = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}"))!;
        while (run["status"]!.GetValue<string>() == "running");
        Assert.Equal("succeeded", run["status"]!.GetValue<string>());
        var view = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{Id(ws)}"))!;
        Assert.Equal(runId, view["runs"]![0]!["runId"]!.GetValue<string>());
    }

    [Fact] // Spine 1: a malformed value is refused before it reaches the log
    public async Task A_malformed_value_is_refused()
    {
        var ws = await Create();

        var response = await Post(ws, "edits", new { revision = Revision(ws), entity = "credit:ABC/LE-01", field = "cvaRecovery", value = 1.5 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("InvalidValue", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }
}
