using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Samples.Rates;

namespace Workbench.Web;

/// <summary>Thin HTTP endpoints over the Rates workspace service and its live subscriptions. No rule lives here.</summary>
public static class RatesApi
{
    public sealed record CreateRequest(DateOnly ValuationDate);
    public sealed record AddLineRequest(long Revision, RatesProduct Product);
    public sealed record AddedLine(string Line, RatesWorkspaceView Workspace);
    public sealed record RevisionRequest(long Revision);
    public sealed record LineEditRequest(long Revision, string Field, JsonNode? Value);
    public sealed record CashflowEditRequest(long Revision, string Period, string Field, JsonNode? Value);
    public sealed record ResolveRequest(long Revision, string Scope, string Entity, string Field, string Resolution);
    public sealed record ValuationDateRequest(long Revision, DateOnly ValuationDate);
    public sealed record Rederived(RatesWorkspaceView Workspace, CarrySummary Carried);
    public sealed record PriceRequest(long Revision, string IdempotencyKey, string MarketSnapshotId);
    public sealed record CaptureRequest(int SubscriptionRevision, long InputRevision, string MarketSnapshotId, string IdempotencyKey);
    public sealed record LiveStarted(string SubscriptionId);
    public sealed record WorkspaceSummary(string WorkspaceId, long Revision, JsonObject Context, int Lines);
    public sealed record CurveView(string Key, string Currency, string Index, IReadOnlyList<PillarView> Pillars);
    public sealed record PillarView(string Tenor, double LevelPercent);
    public sealed record MarketView(string SnapshotId, long Sequence, DateTimeOffset AsOf, string Origin, IReadOnlyList<CurveView> Curves, IReadOnlyDictionary<string, double> Fx);

    public static void MapRatesApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/rates");

        api.MapGet("/market", (MarketFeed feed) => feed.Latest is { } m
            ? Results.Ok(new MarketView(m.Id, m.Sequence, m.AsOf, m.Origin,
                [.. m.Curves.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new CurveView(c.Key, c.Value.Currency, c.Value.Index,
                    [.. c.Value.Pillars.Select(p => new PillarView(p.Tenor.ToString(), p.LevelPercent))]))],
                m.Fx))
            : Results.Json(new Api.ErrorEnvelope("NoMarket", "No market snapshot has been published yet.", [], Retryable: true), statusCode: 503));

        var ws = api.MapGroup("/workspaces");

        ws.MapGet("/", (CurrentUser user, RatesWorkspaces service) =>
            service.OwnedBy(user.Id).Select(w => new WorkspaceSummary(w.WorkspaceId, w.Revision, w.Context, w.Created.Count)));

        ws.MapPost("/", (CreateRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            var created = service.Create(user.Id, request.ValuationDate);
            return Results.Created($"/api/rates/workspaces/{created.WorkspaceId}", service.View(created.WorkspaceId, user.Id));
        }));

        ws.MapGet("/{id}", (string id, CurrentUser user, RatesWorkspaces service) => Guard(() => Results.Ok(service.View(id, user.Id))));

        ws.MapPost("/{id}/lines", (string id, AddLineRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            var (_, line) = service.AddLine(id, request.Revision, user.Id, request.Product);
            return Results.Ok(new AddedLine(line, service.View(id, user.Id)));
        }));

        ws.MapPost("/{id}/lines/{line}/remove", (string id, string line, RevisionRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            service.RemoveLine(id, request.Revision, user.Id, line);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/lines/{line}/edits", (string id, string line, LineEditRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            service.EditLine(id, request.Revision, user.Id, line, request.Field, request.Value);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapGet("/{id}/lines/{line}/cashflows", (string id, string line, CurrentUser user, RatesWorkspaces service) =>
            Guard(() => Results.Ok(service.Cashflows(id, user.Id, line))));

        ws.MapPost("/{id}/lines/{line}/cashflows/edits", (string id, string line, CashflowEditRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            service.EditCashflow(id, request.Revision, user.Id, line, request.Period, request.Field, request.Value);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/resolutions", (string id, ResolveRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            service.Resolve(id, request.Revision, user.Id, request.Scope, request.Entity, request.Field, request.Resolution);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/valuation-date", (string id, ValuationDateRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            var (_, carried) = service.ChangeValuationDate(id, request.Revision, user.Id, request.ValuationDate);
            return Results.Ok(new Rederived(service.View(id, user.Id), carried));
        }));

        ws.MapPost("/{id}/price", (string id, PriceRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
            Submitted(service.Price(id, request.Revision, user.Id, request.IdempotencyKey, request.MarketSnapshotId))));

        ws.MapPost("/{id}/live", (string id, CurrentUser user, RatesWorkspaces service) => Guard(() =>
            Results.Ok(new LiveStarted(service.StartLive(id, user.Id)))));

        ws.MapDelete("/{id}", (string id, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            service.Delete(id, user.Id);
            return Results.NoContent();
        }));

        var live = api.MapGroup("/live");

        // A page attaches while its stream is open: the frame shown now, then the newest only when it falls behind
        // (docs/07 §6). Closing the stream detaches it; the last page to go starts the idle limit.
        live.MapGet("/{sid}/frames", (string sid, CurrentUser user, RatesLiveService service, CancellationToken cancellationToken) => Guard(() =>
        {
            var (frames, detach) = service.Attach(user.Id, sid);
            return TypedResults.ServerSentEvents(Watched(frames, detach, cancellationToken), eventType: "frame");
        }));

        live.MapPost("/{sid}/capture", (string sid, CaptureRequest request, CurrentUser user, RatesWorkspaces service) => Guard(() =>
            Submitted(service.Capture(sid, user.Id, new FrameIdentity(request.SubscriptionRevision, request.InputRevision, request.MarketSnapshotId), request.IdempotencyKey))));

        live.MapDelete("/{sid}", (string sid, CurrentUser user, RatesWorkspaces service) => Guard(() =>
        {
            service.StopLive(sid, user.Id);
            return Results.NoContent();
        }));
    }

    static async IAsyncEnumerable<LiveDelivery> Watched(ChannelReader<LiveDelivery> frames, IDisposable detach,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (detach)
            await foreach (var frame in frames.ReadAllAsync(cancellationToken))
                yield return frame;
    }

    static IResult Submitted(SubmissionResult result) => result switch
    {
        SubmissionResult.Accepted accepted => Results.Accepted($"/api/runs/{accepted.Run.RunId}", accepted.Run),
        SubmissionResult.Rejected rejected => Results.Json(new Api.ErrorEnvelope(rejected.Code, "The Run was not accepted.", rejected.Issues), statusCode: 422),
        _ => throw new InvalidOperationException("Unhandled submission result."),
    };

    static IResult Guard(Func<IResult> handle)
    {
        try
        {
            return handle();
        }
        catch (KeyNotFoundException missing)
        {
            return Results.Json(new Api.ErrorEnvelope("UnknownSubscription", missing.Message, []), statusCode: 404);
        }
        catch (WorkspaceRefused refused)
        {
            var issues = refused.Targets.Select(t => new CompositionIssue(refused.Code, $"/{t.Entity}/{t.Field}", refused.Message)).ToList();
            var status = refused.Code switch
            {
                "UnknownWorkspace" or "UnknownEntity" => 404,
                "NotOwner" => 403,
                "RevisionMoved" or "UnresolvedEdits" or "FrameMoved" or "EntityExists" => 409,
                _ => 422,
            };
            return Results.Json(new Api.ErrorEnvelope(refused.Code, refused.Message, issues), statusCode: status);
        }
    }
}

/// <summary>Advances the synthetic market on a clock, so live subscriptions tick (docs/07 §7). Off when configured so.</summary>
public sealed class MarketTicker(MarketFeed feed, TimeSpan step, ILogger<MarketTicker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await feed.AdvanceAsync();
        using var timer = new PeriodicTimer(step);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await feed.AdvanceAsync();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("The synthetic market ticker stopped.");
        }
    }
}
