using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Workbench.Composition.Tests;

// The HTTP surface maps to the services and refuses what the contract does not know.
public class ApiTests(WorkbenchFactory factory) : IClassFixture<WorkbenchFactory>
{
    readonly HttpClient client = factory.CreateClient();

    async Task<JsonObject> Workflow(string id)
    {
        var catalog = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        return catalog["workflows"]!.AsArray().Single(w => w!["definitionId"]!.GetValue<string>() == id)!.DeepClone().AsObject();
    }

    [Fact]
    public async Task Catalog_lists_what_modules_registered()
    {
        var catalog = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        Assert.Equal(10, catalog["capabilities"]!.AsArray().Count);
        Assert.Contains(catalog["actions"]!.AsArray(), a => a!["target"]!["kind"]!.GetValue<string>() == "openView");
    }

    [Fact]
    public async Task A_run_is_accepted_and_completes()
    {
        var body = new JsonObject
        {
            ["workflow"] = await Workflow("workflow.sample.station-summary"),
            ["context"] = new JsonObject { ["region"] = "North", ["asOf"] = "2026-10-26" },
            ["input"] = new JsonObject { ["days"] = 3 },
            ["idempotencyKey"] = Guid.NewGuid().ToString(),
        };
        var response = await client.PostAsJsonAsync("/api/runs", body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<string>();

        JsonObject run;
        do run = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}"))!;
        while (run["status"]!.GetValue<string>() == "running");
        Assert.Equal("succeeded", run["status"]!.GetValue<string>());

        var summary = await client.PostAsJsonAsync("/api/data/read", run["outputs"]!["summary"]);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
    }

    [Fact]
    public async Task A_workflow_with_an_unknown_member_is_refused()
    {
        var workflow = await Workflow("workflow.sample.station-summary");
        workflow["onComplete"] = "window.location = 'https://example.invalid'";
        var body = new JsonObject
        {
            ["workflow"] = workflow,
            ["context"] = new JsonObject { ["region"] = "North", ["asOf"] = "2026-10-26" },
            ["input"] = new JsonObject { ["days"] = 3 },
            ["idempotencyKey"] = Guid.NewGuid().ToString(),
        };
        var response = await client.PostAsync("/api/runs", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_invalid_input_is_refused_with_its_field()
    {
        var body = new JsonObject
        {
            ["workflow"] = await Workflow("workflow.sample.station-summary"),
            ["context"] = new JsonObject { ["region"] = "North", ["asOf"] = "2026-10-26" },
            ["input"] = new JsonObject { ["days"] = 99 },
            ["idempotencyKey"] = Guid.NewGuid().ToString(),
        };
        var response = await client.PostAsJsonAsync("/api/runs", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("/input/days", error["issues"]![0]!["path"]!.GetValue<string>());
    }
}
