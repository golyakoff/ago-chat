using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationHistoryAsSiteConfigureHolder;

/// <summary>
/// `26-98`: the read `OperatorHub.JoinConversationAsync`/
/// `GetConversationHistory.GetConversationHistoryHandler.HandleAsOperatorAsync` cannot serve - opening
/// any conversation on the caller's own site to read its message history, gated on
/// <see cref="Permission.SiteConfigure"/> (the «Все» list's own permission, `GetAllConversationsForSite.GetAllConversationsForSiteHandler`)
/// rather than an assignment. See <see cref="GetConversationHistoryAsSiteConfigureHolderHandler"/>'s own
/// remarks for why this needed a query and a handler of its own rather than a third entry point on
/// <c>GetConversationHistoryHandler</c> or a reuse of
/// <c>GetVisitorHistory.GetVisitorHistoryHandler.HandleHistoricalConversationAsOperatorAsync</c> (`26-144`).
///
/// <para><c>...Query</c>, not the bare feature name every sibling record in this codebase uses
/// (<c>GetConversationHistoryAsOperator</c>, <c>GetVisitorHistoryConversation</c>) - here the bare name
/// is also this file's own namespace, and a record sharing its namespace's exact name makes an unqualified
/// reference to it ambiguous at any call site that also imports that namespace (CS0118: "is a namespace
/// but is used like a type"). The colliding name is renamed here, never hidden behind a
/// <c>using Alias = ...</c> - the fix this codebase has already standardised on for this exact
/// collision shape.</para>
/// </summary>
public sealed record GetConversationHistoryAsSiteConfigureHolderQuery(
    ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId, int? BeforeSequence, int PageSize);
