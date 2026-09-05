using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOwnAnalyticsForOperator;

/// <summary>
/// `23-18`: an operator's own row of `18-08`/`23-17`'s tenant report, and their own row of `18-10`'s
/// conversion report, on one screen an operator reaches with no `site:configure` grant -
/// `docs/design/flows.md` 2.4's own success test is whether an operator can predict these numbers
/// before their manager mentions them.
///
/// <para><b><see cref="RequestedBy"/> is both "who is asking" and "whose row this returns" - there is
/// no second identifier anywhere on this record.</b> That is deliberate, not an omission: an endpoint
/// that accepted a target operator id and merely checked it matched the caller would still be
/// tamperable in shape even if today's implementation happened to enforce it, so the fix is to leave no
/// parameter to tamper with. <c>ConversationsEndpoints.HandleGetOwnAnalyticsAsync</c> builds this record
/// from <c>HttpContext.User.GetOperatorId()</c> - the validated principal - never from a route segment
/// or a query string.</para>
///
/// <para>Same half-open <paramref name="From"/>/<paramref name="To"/> convention as
/// <c>GetOperatorAnalyticsForSite</c>; either or both <see langword="null"/> defaults the window the
/// identical way (<see cref="GetOwnAnalyticsForOperatorHandler.DefaultWindowDays"/>).</para>
/// </summary>
public sealed record GetOwnAnalyticsForOperator(
    OperatorId RequestedBy, SiteId SiteId, DateTimeOffset? From, DateTimeOffset? To);
