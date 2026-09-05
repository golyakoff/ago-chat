namespace Ago.Chat.Contracts;

/// <summary>
/// `24-12`: `GET /api/v1/sites/{siteId}/access-records`'s response body - one keyset page of who
/// accessed this tenant's data across a recognised boundary-crossing surface. <paramref name="NextBeforeId"/>
/// is the value to pass back as `?before=` for the following page, and <see langword="null"/> once the
/// oldest row has been reached.
/// </summary>
public sealed record AccessRecordsResponse(IReadOnlyList<AccessRecordDto> Records, Guid? NextBeforeId);

/// <summary>
/// One access, as read back. Carries only who/what-kind/when/which-resource - never what was actually
/// read (`IAccessRecordRepository`'s own remarks: "record that a read happened and by whom - not what
/// was returned").
/// </summary>
/// <param name="ActorKind">`"Operator"` or `"PlatformOwner"` - <see cref="ActorId"/>'s own vocabulary
/// (an <c>OperatorId</c> in the first case, a Keycloak `sub` in the second - the platform owner has no
/// <c>operators</c> row, `adr/0032`).</param>
/// <param name="ResourceKind">Which table <see cref="ResourceId"/> names - `"Conversation"`,
/// `"ChannelIdentity"`, `"EnabledModule"`, or <see langword="null"/> when the access named no single
/// resource (the platform owner's cross-tenant list, or a per-tenant detail read already named by the
/// row's own site).</param>
public sealed record AccessRecordDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string AccessKind,
    string ActorKind,
    string ActorId,
    string? ResourceKind,
    Guid? ResourceId);
