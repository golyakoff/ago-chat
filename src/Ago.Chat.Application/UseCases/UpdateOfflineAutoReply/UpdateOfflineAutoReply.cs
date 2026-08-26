using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UpdateOfflineAutoReply;

/// <summary>
/// `14-04`. The rules arrive as raw <c>(keyword, reply)</c> string pairs, not yet the validated
/// <see cref="OfflineAutoReplyRule"/> - <see cref="UpdateOfflineAutoReplyHandler"/> is what turns a
/// bad one into a clean <c>Result</c> failure instead of an unhandled exception, the same split
/// `11-01`'s <c>UpdateWidgetConfigHandler</c> draws for a hex colour.
/// </summary>
public sealed record UpdateOfflineAutoReply(
    SiteId SiteId,
    OperatorId RequestedBy,
    bool Enabled,
    string FallbackReply,
    IReadOnlyList<UpdateOfflineAutoReplyRule> Rules);

/// <summary>The unvalidated wire shape of one rule - see <see cref="UpdateOfflineAutoReply"/>.</summary>
public sealed record UpdateOfflineAutoReplyRule(string Keyword, string Reply);
