using Ago.Chat.Api.Documents;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.PublishDocumentVersion;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `24-02`: the one procedure by which a document's text ever reaches production - not an edit
/// somebody makes to a file, an authenticated call the platform owner makes once `ago-business` and a
/// lawyer have signed off on the wording. See <see cref="PublishDocumentVersionHandler"/>'s own remarks
/// for why that is the whole mechanism, and this endpoint's own class remarks below for who is trusted
/// to invoke it.
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story,
/// the same single-gate shape every other owner surface in this codebase already uses
/// (<see cref="OwnerModuleEndpoints"/>'s own remarks): neither handler this file resolves calls
/// <see cref="Application.Abstractions.IPermissionChecker"/>, and could not - a document is not scoped
/// to any one tenant for a permission check to be evaluated against in the first place. "The named
/// owner" `24-02`'s own Done-when asks for is, concretely, whoever holds this codebase's
/// <c>platform-owner</c> Keycloak realm role - the same identity every other <c>/owner/</c> route
/// already trusts with a cross-tenant action.</para>
///
/// <para><b>A deliberately separate route and file from <see cref="DocumentEndpoints"/></b> -
/// the same "`/owner/` stays the platform owner's own namespace, never blurred with a public route"
/// discipline <see cref="OwnerChannelIdentityEndpoints"/>'s own remarks state for itself, even though
/// both ultimately reach the identical <c>Document</c> aggregate.</para>
/// </summary>
public static class OwnerDocumentEndpoints
{
    public static void MapOwnerDocumentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/owner/documents", HandlePublishAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandlePublishAsync(
        PublishDocumentRequest request, PublishDocumentVersionHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new PublishDocumentVersion(request.DocumentKey, request.Title, request.Body), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var dto = result.Value;
        return Results.Ok(new PublishedDocumentResponse(dto.DocumentKey, dto.Version, dto.Sequence, dto.Title, dto.Body, dto.PublishedAt));
    }

    public sealed record PublishDocumentRequest(string DocumentKey, string Title, string Body);

    public sealed record PublishedDocumentResponse(
        string DocumentKey, string Version, int Sequence, string Title, string Body, DateTimeOffset PublishedAt);
}
