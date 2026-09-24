using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAllConversationsForSite;

/// <summary>`5-08`: the admin/supervisor view's own query - <paramref name="BeforeId"/>
/// <see langword="null"/> means "most recent page" (same convention `GetConversationHistoryAsOperator`'s
/// `BeforeSequence` already uses). <paramref name="Tag"/>: `18-04`'s own list filter, pushed into
/// <see cref="Application.Abstractions.IConversationReadStore.GetAllForSiteAsync"/>'s own query rather
/// than filtered in memory afterward - see that method's own remarks on why this read, unlike
/// `GetOperatorQueueHandler`'s two, is genuinely paginated and cannot be filtered after the
/// page.</summary>
/// <param name="States">`26-90`: the state filter behind Android's "Все" tab - raw
/// <see cref="ConversationState"/> member names as they arrived on the wire, <see langword="null"/> or
/// empty meaning unfiltered. Strings, not the domain enum, for the reason
/// <c>GetSiteConsentAcceptances</c>'s own <c>Purpose</c> is one: a query record is the shape a caller
/// sent, and rejecting a value that is not a state is the handler's answer to give as a
/// <see cref="Ago.Platform.Kernel.Error"/>, not the HTTP layer's to give as a binding failure - which
/// would spend the endpoint's whole vocabulary on "400, unparseable" and lose the chance to say which
/// value was wrong and what the valid ones are.</param>
public sealed record GetAllConversationsForSite(
    OperatorId RequestedBy, SiteId SiteId, Guid? BeforeId, int PageSize, TagId? Tag = null,
    IReadOnlyList<string>? States = null);
