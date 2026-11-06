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

    async Task<(string Id, long Revision)> Workspace()
    {
        var ws = await Ok(await client.PostAsJsonAsync("/api/rates/workspaces", new { valuationDate = "2026-10-27" }));
        return (ws["workspaceId"]!.GetValue<string>(), ws["revision"]!.GetValue<long>());
    }

    static JsonObject LineOf(JsonObject added) =>
        added["workspace"]!["lines"]!.AsArray().Single(l => l!["line"]!.GetValue<string>() == added["line"]!.GetValue<string>())!.AsObject();

    [Fact] // ADR-0008 §1, §3: `new irs` reads its words by form in any order; ATM by C47; the profile's template is recorded as such
    public async Task A_new_irs_is_read_from_short_words_and_records_its_defaults()
    {
        var (id, revision) = await Workspace();

        var added = await Ok(await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/new",
            new { revision, product = "rates.irs", terms = "jpy 10y 3bn rec" }));

        var line = LineOf(added);
        Assert.True(line["priceable"]!.GetValue<bool>(), line.ToJsonString());
        Assert.Equal(3e9, line["fields"]!["leg2.notional"]!.GetValue<double>());
        Assert.Equal("TONA", line["fields"]!["leg2.index"]!.GetValue<string>());
        var defaulted = line["defaulted"]!.AsObject();
        Assert.StartsWith("profile:proposed.jpy.tona@", defaulted["leg1.index"]!.GetValue<string>());
        Assert.StartsWith("profile:proposed.jpy.tona@", defaulted["leg1.paymentFrequency"]!.GetValue<string>());
        Assert.Equal("C47", defaulted["leg2.quote"]!.GetValue<string>());
        Assert.False(defaulted.ContainsKey("leg1.notional"));
    }

    [Fact] // ADR-0008 §1, §4c: a term no default supplies is refused by name, and nothing is written
    public async Task Missing_terms_are_refused_by_name()
    {
        var (id, revision) = await Workspace();

        var response = await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/new", new { revision, product = "rates.irs", terms = "10y" });

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var paths = (await response.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()).ToList();
        // The frequencies come from the currency's profile, so with no currency they are missing too.
        Assert.Equal(["/terms/currency", "/terms/index", "/terms/notional", "/terms/direction", "/terms/fixedFrequency", "/terms/floatFrequency"], paths);
        var view = (await client.GetFromJsonAsync<JsonObject>($"/api/rates/workspaces/{id}"))!;
        Assert.Equal(revision, view["revision"]!.GetValue<long>());
        Assert.Empty(view["lines"]!.AsArray());
    }

    [Fact] // ADR-0008 §4b: the workspace's own defaults fill what the user left out, and say they did
    public async Task The_workspace_defaults_fill_a_new_irs()
    {
        var (id, revision) = await Workspace();
        var view = await Ok(await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/defaults",
            new { revision, currency = "USD", notional = 500_000_000, direction = "pay" }));
        Assert.Equal("USD", view["defaults"]!["currency"]!.GetValue<string>());

        var added = await Ok(await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/new",
            new { revision = view["revision"]!.GetValue<long>(), product = "rates.irs", terms = "5y" }));

        var line = LineOf(added);
        Assert.True(line["priceable"]!.GetValue<bool>(), line.ToJsonString());
        Assert.Equal("SOFR", line["fields"]!["leg1.index"]!.GetValue<string>());
        Assert.Equal("workspace", line["defaulted"]!["leg1.notional"]!.GetValue<string>());
        Assert.Equal("workspace", line["defaulted"]!["direction"]!.GetValue<string>());
    }

    [Fact] // ADR-0008 §7: a CCS with no basis spread reads, but the economic check still refuses it; nothing defaults one
    public async Task A_new_ccs_without_a_spread_is_refused_by_its_economic_check()
    {
        var (id, revision) = await Workspace();

        var response = await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/new",
            new { revision, product = "rates.ccs", terms = "5y usd jpy 100 15bn pay" });

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Contains("par", (await response.Content.ReadFromJsonAsync<JsonObject>())!["message"]!.GetValue<string>());
    }

    [Fact] // ADR-0008 §2: a CCS's currencies and notionals are sequences, leg 1 then leg 2
    public async Task A_new_ccs_takes_its_legs_in_order()
    {
        var (id, revision) = await Workspace();

        var added = await Ok(await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/new",
            new { revision, product = "rates.ccs", terms = "5y usd jpy 100 15bn pay spread2=-30bp" }));

        var fields = LineOf(added)["fields"]!;
        Assert.Equal(("USD", "JPY"), (fields["leg1.currency"]!.GetValue<string>(), fields["leg2.currency"]!.GetValue<string>()));
        Assert.Equal((100e6, 15e9), (fields["leg1.notional"]!.GetValue<double>(), fields["leg2.notional"]!.GetValue<double>()));
        Assert.Equal(("SOFR", "TONA"), (fields["leg1.index"]!.GetValue<string>(), fields["leg2.index"]!.GetValue<string>()));
    }

    [Fact] // ADR-0008 §2 (owner, 2026-11-04): a full amount typed where millions are read is refused, not made a quadrillion
    public async Task A_full_amount_is_refused()
    {
        var (id, revision) = await Workspace();

        var response = await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/new",
            new { revision, product = "rates.irs", terms = "jpy 10y 3000000000 pay" });

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Contains((await response.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray(),
            i => i!["path"]!.GetValue<string>() == "/terms/notional");
    }
}
