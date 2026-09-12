using Ago.Chat.Api.Documents;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.AddRequiredDocument;
using Ago.Chat.Application.UseCases.GetRequiredDocumentsForSubjectKind;
using Ago.Chat.Application.UseCases.PublishDocumentVersion;
using Ago.Chat.Application.UseCases.RemoveRequiredDocument;
using Ago.Chat.Domain;

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
///
/// <para><b>`24-16`: three more routes join the publish route above</b> - list/add/remove over
/// `required_documents`, the surface `24-03`'s own port docstring named as a future item ("a dedicated
/// owner-facing endpoint is a future, separate item's job"). Same file, same single
/// <c>RequirePlatformOwner</c> gate, for the identical reason the publish route needs no
/// <see cref="Application.Abstractions.IPermissionChecker"/> check: a required-document row is not
/// scoped to any one tenant either. The list route resolves the identical
/// <see cref="GetRequiredDocumentsForSubjectKindHandler"/> <see cref="DocumentEndpoints"/>'s own
/// unauthenticated route already uses - not a second read implementation, only a second, owner-gated
/// door onto the same small, non-sensitive answer ("what must this subject kind accept, and what does
/// each key currently say"), so a platform owner managing this list never has to leave their own
/// authenticated surface to see the effect of their last write.</para>
/// </summary>
public static class OwnerDocumentEndpoints
{
    public static void MapOwnerDocumentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/owner/documents", HandlePublishAsync)
            .RequireAuthorization("RequirePlatformOwner");

        app.MapGet("/api/v1/owner/documents/required/{subjectKind}", HandleListRequiredAsync)
            .RequireAuthorization("RequirePlatformOwner");
        app.MapPost("/api/v1/owner/documents/required", HandleAddRequiredAsync)
            .RequireAuthorization("RequirePlatformOwner");
        app.MapDelete("/api/v1/owner/documents/required/{subjectKind}/{documentKey}", HandleRemoveRequiredAsync)
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

    private static async Task<IResult> HandleListRequiredAsync(
        string subjectKind, GetRequiredDocumentsForSubjectKindHandler handler, CancellationToken cancellationToken)
    {
        if (!TryParseSubjectKind(subjectKind, out var kind, out var problem))
        {
            return problem;
        }

        var summaries = await handler.HandleAsync(new GetRequiredDocumentsForSubjectKind(kind), cancellationToken);
        return Results.Ok(summaries
            .Select(s => new RequiredDocumentResponse(s.DocumentKey, s.Version, s.Title, s.PublishedAt))
            .ToList());
    }

    private static async Task<IResult> HandleAddRequiredAsync(
        AddRequiredDocumentRequest request, AddRequiredDocumentHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!TryParseSubjectKind(request.SubjectKind, out var kind, out var problem))
        {
            return problem;
        }

        var result = await handler.HandleAsync(new AddRequiredDocument(kind, request.DocumentKey), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var dto = result.Value;
        return Results.Ok(new AddedRequiredDocumentResponse(dto.SubjectKind.ToString(), dto.DocumentKey, dto.AlreadyRequired));
    }

    private static async Task<IResult> HandleRemoveRequiredAsync(
        string subjectKind, string documentKey, RemoveRequiredDocumentHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!TryParseSubjectKind(subjectKind, out var kind, out var problem))
        {
            return problem;
        }

        var result = await handler.HandleAsync(new RemoveRequiredDocument(kind, documentKey), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var dto = result.Value;
        return Results.Ok(new RemovedRequiredDocumentResponse(dto.SubjectKind.ToString(), dto.DocumentKey, dto.WasRequired));
    }

    /// <summary>The identical "a route-segment string that fails to parse is the caller's own mistake"
    /// reasoning <see cref="DocumentEndpoints.HandleGetRequiredDocumentsAsync"/> already gives for its
    /// own unauthenticated sibling - restated here so all three owner-gated routes above share one
    /// parse, rather than three near-identical copies of the same <c>Enum.TryParse</c> and the same
    /// `400`.</summary>
    private static bool TryParseSubjectKind(string subjectKind, out AcceptanceSubjectKind kind, out IResult problem)
    {
        if (Enum.TryParse<AcceptanceSubjectKind>(subjectKind, ignoreCase: true, out kind))
        {
            problem = null!;
            return true;
        }

        problem = Results.Problem(
            title: "Document.InvalidSubjectKind",
            detail: $"'{subjectKind}' is not a known subject kind.",
            statusCode: StatusCodes.Status400BadRequest,
            type: "Document.InvalidSubjectKind");
        return false;
    }

    public sealed record PublishDocumentRequest(string DocumentKey, string Title, string Body);

    public sealed record PublishedDocumentResponse(
        string DocumentKey, string Version, int Sequence, string Title, string Body, DateTimeOffset PublishedAt);

    /// <summary>`24-16`'s own wire shape for the list route - identical field set to
    /// <see cref="DocumentEndpoints.RequiredDocumentResponse"/>, deliberately its own type rather than a
    /// shared one: per api-design.md, the same divergence-allowed boundary every other pair of
    /// owner/public response records in this codebase already accepts, so a future change to one
    /// surface's own wire shape never silently moves the other's.</summary>
    public sealed record RequiredDocumentResponse(string DocumentKey, string? Version, string? Title, DateTimeOffset? PublishedAt);

    public sealed record AddRequiredDocumentRequest(string SubjectKind, string DocumentKey);

    public sealed record AddedRequiredDocumentResponse(string SubjectKind, string DocumentKey, bool AlreadyRequired);

    public sealed record RemovedRequiredDocumentResponse(string SubjectKind, string DocumentKey, bool WasRequired);
}
