using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// The Rates line pricer over HTTP (docs/07, docs/10): commands carry their revision; Price names its snapshot.
public class RatesApiTests : IClassFixture<WorkbenchFactory>
{
    readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host;
    readonly HttpClient client;

    public RatesApiTests(WorkbenchFactory factory)
    {
        host = factory.WithWebHostBuilder(b => b.UseSetting("Rates:Ticker", "false"));
        client = host.CreateClient();
    }

    async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    async Task<(string Id, JsonObject View, string Line)> PricedLine()
    {
        var ws = await Ok(await client.PostAsJsonAsync("/api/rates/workspaces", new { valuationDate = "2026-10-27" }));
        var id = ws["workspaceId"]!.GetValue<string>();
        var added = await Ok(await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines", new { revision = ws["revision"]!.GetValue<long>(), product = "irs" }));
        var line = added["line"]!.GetValue<string>();
        var view = added["workspace"]!.AsObject();
        foreach (var (field, value) in new (string, object?)[]
        {
            ("end", "5y"), ("leg1.currency", "JPY"), ("leg1.index", "TONA"), ("leg1.notional", 1_000_000_000), ("leg1.paymentFrequency", "12m"),
            ("leg2.currency", "JPY"), ("leg2.index", "TONA"), ("leg2.notional", 1_000_000_000), ("leg2.paymentFrequency", "12m"),
            ("leg2.quoteKind", "FloatSpreadPercent"), ("leg2.quote", 0),
        })
            view = await Ok(await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/{line}/edits",
                new { revision = view["revision"]!.GetValue<long>(), field, value }));
        return (id, view, line);
    }

    [Fact] // docs/07 §3: a line typed through the API prices once against the snapshot the request names
    public async Task A_line_is_priced_against_the_named_snapshot()
    {
        var (id, view, line) = await PricedLine();
        var snapshot = await host.Services.GetRequiredService<MarketFeed>().AdvanceAsync();
        Assert.True(view["lines"]!.AsArray().Single(l => l!["line"]!.GetValue<string>() == line)!["priceable"]!.GetValue<bool>());

        var response = await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/price",
            new { revision = view["revision"]!.GetValue<long>(), idempotencyKey = "k1", marketSnapshotId = snapshot.Id });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(snapshot.Id, (await response.Content.ReadFromJsonAsync<JsonObject>())!["input"]!["marketSnapshotId"]!.GetValue<string>());
    }

    [Fact] // Spine 6: an edit against a moved revision is refused, whatever the timing
    public async Task An_edit_against_a_moved_revision_is_refused()
    {
        var (id, view, line) = await PricedLine();

        var response = await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/{line}/edits",
            new { revision = view["revision"]!.GetValue<long>() - 1, field = "end", value = "7y" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact] // docs/07 §6: frames reach the client as server-sent events, each naming its revision and snapshot
    public async Task Live_frames_stream_as_server_sent_events()
    {
        var (id, view, _) = await PricedLine();
        var started = await Ok(await client.PostAsync($"/api/rates/workspaces/{id}/live", null));
        var subscription = started["subscriptionId"]!.GetValue<string>();
        // The frame waits in the subscription's channel until the stream reads it, so the order cannot race.
        var snapshot = await host.Services.GetRequiredService<MarketFeed>().AdvanceAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = await client.GetStreamAsync($"/api/rates/live/{subscription}/frames", timeout.Token);
        using var reader = new StreamReader(stream);
        string? data = null;
        while (data is null && await reader.ReadLineAsync(timeout.Token) is { } text)
            if (text.StartsWith("data: ", StringComparison.Ordinal)) data = text["data: ".Length..];

        var frame = JsonNode.Parse(data!)!;
        Assert.Equal(view["revision"]!.GetValue<long>(), frame["inputRevision"]!.GetValue<long>());
        Assert.Equal(snapshot.Id, frame["market"]!["snapshotId"]!.GetValue<string>());
        Assert.Equal("simulated", frame["calculationMode"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/rates/live/{subscription}")).StatusCode);
    }

    [Fact] // ADR-0006: the command line makes a line from its terms at the revision the page saw, and completes legs from the conventions
    public async Task A_line_is_created_from_its_terms_at_the_page_revision()
    {
        var conventions = (await client.GetFromJsonAsync<JsonArray>("/api/rates/conventions"))!;
        Assert.Contains(conventions, c => c!["currency"]!.GetValue<string>() == "JPY" && c["index"]!.GetValue<string>() == "TONA");

        var ws = await Ok(await client.PostAsJsonAsync("/api/rates/workspaces", new { valuationDate = "2026-10-27" }));
        var id = ws["workspaceId"]!.GetValue<string>();
        var revision = ws["revision"]!.GetValue<long>();
        var terms = new JsonObject
        {
            ["product"] = "irs", ["end"] = "10y", ["direction"] = "pay",
            ["leg1.currency"] = "JPY", ["leg1.index"] = "TONA", ["leg1.notional"] = 3_000_000_000, ["leg1.paymentFrequency"] = "12m",
            ["leg2.currency"] = "JPY", ["leg2.index"] = "TONA", ["leg2.notional"] = 3_000_000_000, ["leg2.paymentFrequency"] = "12m",
            ["leg2.quoteKind"] = "FloatSpreadPercent", ["leg2.quote"] = 0,
        };

        var stale = await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/terms", new { revision = revision - 1, terms });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var added = await Ok(await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/terms", new { revision, terms }));
        var line = added["line"]!.GetValue<string>();
        Assert.True(added["workspace"]!["lines"]!.AsArray().Single(l => l!["line"]!.GetValue<string>() == line)!["priceable"]!.GetValue<bool>());
        var log = host.Services.GetRequiredService<Workbench.Composition.Application.Workspaces.WorkspaceService>().Find(id)!.Log;
        Assert.Equal(Workbench.Composition.Application.RunOrigin.Pricer, log.Single(e => e.Kind == Workbench.Composition.Application.Workspaces.EditKind.Create).Origin);
    }
}
