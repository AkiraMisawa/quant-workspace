using Workbench.Composition.Application.Products;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Application;

namespace Workbench.Web;

/// <summary>The described products (ADR-0008), for the command line to offer and to show what typed words make.</summary>
public static class ProductsApi
{
    public sealed record PreviewRequest(string WorkspaceId, string Terms);

    public static void MapProductsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/products");

        api.MapGet("/", (string? app, ProductService products) => products.Creatable(app));

        // What `new <product> <terms>` would make in this workspace, read by the server (ADR-0008 §6); creates nothing.
        api.MapPost("/{id}/preview", (string id, PreviewRequest request, CurrentUser user, ProductService products) =>
        {
            try
            {
                return Results.Ok(products.Preview(request.WorkspaceId, user.Id, id, request.Terms));
            }
            catch (WorkspaceRefused refused)
            {
                return Results.Json(new Api.ErrorEnvelope(refused.Code, refused.Message, []),
                    statusCode: refused.Code switch { "UnknownWorkspace" or "UnknownProduct" => 404, "NotOwner" => 403, _ => 422 });
            }
        });
    }

    public static Api.ErrorEnvelope Envelope(ProductTermsRefused refused) => new("InvalidTerms", refused.Message,
        [.. refused.Issues.Select(i => new CompositionIssue(i.Code, i.Field is null ? "/terms" : $"/terms/{i.Field}", i.Message))]);
}
