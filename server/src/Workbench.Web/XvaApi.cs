using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Samples.Xva;

namespace Workbench.Web;

/// <summary>Thin HTTP endpoints over the XVA workspace service. No rule lives here.</summary>
public static class XvaApi
{
    public sealed record CreateRequest(string Registration, DateOnly ValueDate, int PortfolioSize);
    public sealed record PageRequest(int Start, int Count, IReadOnlyList<RowSort> Sorts);
    public sealed record EditRequest(long Revision, string Entity, string Field, JsonNode? Value);
    public sealed record ResolveRequest(long Revision, string Entity, string Field, string Resolution);
    public sealed record ValueDateRequest(long Revision, DateOnly ValueDate);
    public sealed record CounterpartyRequest(long Revision, string Registration, int ConfirmedCount);
    public sealed record PriceRequest(long Revision, string IdempotencyKey);
    public sealed record Reacquired(XvaWorkspaceView Workspace, CarrySummary Carried);
    public sealed record WorkspaceSummary(string WorkspaceId, long Revision, JsonObject Context);

    /// <summary>The largest page a request may ask for; ExGrid asks for its Window plus read-ahead.</summary>
    const int MaxPage = 2000;

    public static void MapXvaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/xva");

        api.MapGet("/registrations", () => XvaCatalog.Registrations.Select(r => new
        {
            r.Key, r.Code, r.LegalEntity, r.Name, Agreement = XvaCatalog.AgreementOf(r.Key),
        }));

        var ws = api.MapGroup("/workspaces");

        ws.MapGet("/", (CurrentUser user, XvaWorkspaces service) =>
            service.OwnedBy(user.Id).Select(w => new WorkspaceSummary(w.WorkspaceId, w.Revision, w.Context)));

        ws.MapPost("/", (CreateRequest request, CurrentUser user, XvaWorkspaces service) => Guard(() =>
        {
            if (request.PortfolioSize is < 0 or > 100_000) throw new WorkspaceRefused("InvalidValue", "The portfolio size is from 0 to 100,000.");
            var created = service.Create(user.Id, request.Registration, request.ValueDate, request.PortfolioSize);
            return Results.Created($"/api/xva/workspaces/{created.WorkspaceId}", service.View(created.WorkspaceId, user.Id));
        }));

        ws.MapGet("/{id}", (string id, CurrentUser user, XvaWorkspaces service) =>
            Guard(() => Results.Ok(service.View(id, user.Id))));

        ws.MapPost("/{id}/rows", (string id, PageRequest request, CurrentUser user, XvaWorkspaces service) => Guard(() =>
        {
            if (request.Count > MaxPage) throw new WorkspaceRefused("InvalidRange", $"A page holds at most {MaxPage} rows.");
            return Results.Ok(service.Page(id, user.Id, request.Start, request.Count, request.Sorts));
        }));

        ws.MapPost("/{id}/edits", (string id, EditRequest request, CurrentUser user, XvaWorkspaces service) => Guard(() =>
        {
            service.Edit(id, request.Revision, user.Id, request.Entity, request.Field, request.Value);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/resolutions", (string id, ResolveRequest request, CurrentUser user, XvaWorkspaces service) => Guard(() =>
        {
            service.Resolve(id, request.Revision, user.Id, request.Entity, request.Field, request.Resolution);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/value-date", (string id, ValueDateRequest request, CurrentUser user, XvaWorkspaces service) => Guard(() =>
        {
            var (_, carried) = service.ChangeValueDate(id, request.Revision, user.Id, request.ValueDate);
            return Results.Ok(new Reacquired(service.View(id, user.Id), carried));
        }));

        ws.MapGet("/{id}/counterparty-change", (string id, CurrentUser user, XvaWorkspaces service) =>
            Guard(() => Results.Ok(service.PreviewCounterpartyChange(id, user.Id))));

        ws.MapPost("/{id}/counterparty", (string id, CounterpartyRequest request, CurrentUser user, XvaWorkspaces service) => Guard(() =>
        {
            var (_, carried) = service.ChangeCounterparty(id, request.Revision, user.Id, request.Registration, request.ConfirmedCount);
            return Results.Ok(new Reacquired(service.View(id, user.Id), carried));
        }));

        ws.MapPost("/{id}/price", (string id, PriceRequest request, CurrentUser user, XvaWorkspaces service) => Guard(() =>
            service.Price(id, request.Revision, user.Id, request.IdempotencyKey) switch
            {
                SubmissionResult.Accepted accepted => Results.Accepted($"/api/runs/{accepted.Run.RunId}", accepted.Run),
                SubmissionResult.Rejected rejected => Results.Json(new Api.ErrorEnvelope(rejected.Code, "The Run was not accepted.", rejected.Issues), statusCode: 422),
                _ => throw new InvalidOperationException("Unhandled submission result."),
            }));

        ws.MapDelete("/{id}", (string id, CurrentUser user, XvaWorkspaces service) => Guard(() =>
        {
            service.Delete(id, user.Id);
            return Results.NoContent();
        }));
    }

    static IResult Guard(Func<IResult> handle)
    {
        try
        {
            return handle();
        }
        catch (WorkspaceRefused refused)
        {
            var issues = refused.Targets.Select(t => new CompositionIssue(refused.Code, $"/{t.Entity}/{t.Field}", refused.Message)).ToList();
            var status = refused.Code switch
            {
                "UnknownWorkspace" => 404,
                "NotOwner" => 403,
                "RevisionMoved" or "ConfirmationStale" or "UnresolvedEdits" or "ConflictUnresolved" => 409,
                _ => 422,
            };
            return Results.Json(new Api.ErrorEnvelope(refused.Code, refused.Message, issues), statusCode: status);
        }
    }
}

/// <summary>
/// The user a request acts as. Until sign-in through ME-ID (C30) is wired, every request acts as the one
/// configured demonstration user; nothing here pretends to authenticate anyone.
/// </summary>
public sealed record CurrentUser(string Id);
