using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.AddPersonNote;
using Ago.Chat.Application.UseCases.GetPersonNotes;
using Ago.Chat.Application.UseCases.GetPersons;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Persons;

/// <summary>
/// `adr/0184`: the account's person registry, read for display - `GET /api/v1/persons?ids=a,b,c`,
/// `GET /api/v1/persons/{personId}`, and the operator's notes about a person under
/// `GET`/`POST /api/v1/persons/{personId}/notes` (author decision O3). The only HTTP surface that reaches
/// <c>GetPersonsHandler</c>/<c>IPersonNoteRepository</c>, operator-only: <c>ago-console</c> and
/// <c>ago-android</c> call it with the person ids a calendar screen carries and merge the answer onto that
/// screen client-side (decision 4). No other product calls it; the calendar never does.
///
/// <para>The route is <c>/persons</c>, not <c>/visitors</c>, on purpose: this is the elevated concept
/// (`adr/0184` decision 1), and the wire is the one place the rename costs nothing. The type underneath
/// is still <c>Visitor</c> (the `adr/0093` Site->Account precedent - the rename is deferred, the concept
/// is not).</para>
/// </summary>
public static class PersonEndpoints
{
    public static void MapPersonEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/persons").RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleListAsync);
        group.MapGet("/{personId:guid}", HandleGetAsync);
        group.MapGet("/{personId:guid}/notes", HandleGetNotesAsync);
        group.MapPost("/{personId:guid}/notes", HandleAddNoteAsync);
    }

    /// <summary><c>?ids=</c> is comma-separated - the batch a screen needs, in one request. Ids that do
    /// not parse are ignored rather than failing the whole batch; ids that resolve to nobody in this
    /// account are simply absent from the answer (GetPersonsHandler's own remarks).</summary>
    private static async Task<IResult> HandleListAsync(
        string? ids, GetPersonsHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var personIds = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(raw => Guid.TryParse(raw, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => new VisitorId(id!.Value))
            .ToList();

        var result = await handler.HandleAsync(new GetPersons(user.GetSiteId(), user.GetOperatorId(), personIds), cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new PersonsResponse(result.Value));
    }

    private static async Task<IResult> HandleGetAsync(
        Guid personId, GetPersonsHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetPersons(user.GetSiteId(), user.GetOperatorId(), [new VisitorId(personId)]), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var profile = result.Value.FirstOrDefault();
        return profile is null
            ? Application.UseCases.PersonErrors.NotFound(personId).ToProblem(httpContext)
            : Results.Ok(profile);
    }

    private static async Task<IResult> HandleGetNotesAsync(
        Guid personId, GetPersonNotesHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetPersonNotes(new VisitorId(personId), user.GetSiteId(), user.GetOperatorId()), cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new PersonNotesResponse(result.Value));
    }

    private static async Task<IResult> HandleAddNoteAsync(
        Guid personId,
        AddPersonNoteRequest request,
        AddPersonNoteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new AddPersonNote(new VisitorId(personId), user.GetSiteId(), user.GetOperatorId(), request.Body ?? string.Empty),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new PersonNoteDto(result.Value.Id, result.Value.AuthorId, result.Value.Body, result.Value.CreatedAt));
    }

    public sealed record AddPersonNoteRequest(string? Body);

    public sealed record PersonsResponse(IReadOnlyList<PersonProfileDto> Persons);

    public sealed record PersonNotesResponse(IReadOnlyList<PersonNoteDto> Notes);
}
