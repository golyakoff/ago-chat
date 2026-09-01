using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetModuleTaskChannelPriorityList;

/// <summary>`20-11`: sets (or clears, with an empty list) the visitor's own priority-ordered list of
/// additional contact channels for the conversation's current active booking (module task). The order of
/// <see cref="ChannelIdentityIdsInPriorityOrder"/> *is* the priority - first entry highest - matching how
/// <c>SetModuleTaskChannelPriorityListHandler</c> assigns <see cref="Domain.ModuleTaskChannelPreference.Priority"/>.</summary>
public sealed record SetModuleTaskChannelPriorityList(
    OperatorId RequestedBy,
    SiteId SiteId,
    ConversationId ConversationId,
    IReadOnlyList<ChannelIdentityId> ChannelIdentityIdsInPriorityOrder);
