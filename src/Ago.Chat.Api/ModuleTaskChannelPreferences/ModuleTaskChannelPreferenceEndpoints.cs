using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.ListModuleTaskChannelPriorityList;
using Ago.Chat.Application.UseCases.SetModuleTaskChannelPriorityList;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.ModuleTaskChannelPreferences;

/// <summary>
/// `20-11`: the per-booking priority list's own console surface - `GET`/`PUT
/// .../module-task-channel-priority`, the identical `RequireOperatorIdentity`/route shape
/// `ChannelIdentityEndpoints`' own `/preference` route already establishes for `14-13`'s single-value
/// sibling. No console page renders this yet (`20-11`'s own report: no existing per-conversation "active
/// booking" surface exists to attach one to) - these endpoints exist so the backend half of this item is
/// complete and independently testable ahead of that UI work, the same "endpoint before console" split
/// `PhoneVerificationEndpoints` itself started from.
/// </summary>
public static class ModuleTaskChannelPreferenceEndpoints
{
    public static void MapModuleTaskChannelPreferenceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/conversations/{conversationId:guid}/module-task-channel-priority")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleListAsync);
        // `PUT`, not `POST` - the body carries the entire state being asserted ("this booking's priority
        // list is now exactly this order"), the identical reasoning `ChannelIdentityEndpoints`' own
        // `/preference` route gives for the same choice.
        group.MapPut("", HandleSetAsync);
    }

    private static async Task<IResult> HandleListAsync(
        Guid conversationId,
        ListModuleTaskChannelPriorityListHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ListModuleTaskChannelPriorityList(new ConversationId(conversationId), user.GetOperatorId(), user.GetSiteId()),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ModuleTaskChannelPriorityListResponse([.. result.Value.Select(ToDto)]));
    }

    private static async Task<IResult> HandleSetAsync(
        Guid conversationId,
        SetModuleTaskChannelPriorityRequest request,
        SetModuleTaskChannelPriorityListHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var ids = (request.ChannelIdentityIdsInPriorityOrder ?? [])
            .Select(id => new ChannelIdentityId(id))
            .ToList();
        var result = await handler.HandleAsync(
            new SetModuleTaskChannelPriorityList(
                user.GetOperatorId(), user.GetSiteId(), new ConversationId(conversationId), ids),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static ModuleTaskChannelPreferenceDto ToDto(ModuleTaskChannelPreferenceSummary s) =>
        new(s.ChannelIdentityId, s.Kind.ToString(), s.Address, s.Priority, s.AddedAt, s.IsActive);

    public sealed record SetModuleTaskChannelPriorityRequest(IReadOnlyList<Guid>? ChannelIdentityIdsInPriorityOrder);

    public sealed record ModuleTaskChannelPreferenceDto(
        Guid ChannelIdentityId, string Kind, string Address, int Priority, DateTimeOffset AddedAt, bool IsActive);

    public sealed record ModuleTaskChannelPriorityListResponse(IReadOnlyList<ModuleTaskChannelPreferenceDto> Entries);
}
