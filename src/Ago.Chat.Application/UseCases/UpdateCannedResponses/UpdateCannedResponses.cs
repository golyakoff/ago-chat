using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UpdateCannedResponses;

/// <summary>
/// `18-03`. The responses arrive as raw <c>(title, body)</c> string pairs, not yet the validated
/// <see cref="CannedResponse"/> - <see cref="UpdateCannedResponsesHandler"/> is what turns a bad one
/// into a clean <c>Result</c> failure instead of an unhandled exception, the same split
/// `UpdateOfflineAutoReplyHandler` draws for its own rules.
/// </summary>
public sealed record UpdateCannedResponses(
    SiteId SiteId,
    OperatorId RequestedBy,
    IReadOnlyList<UpdateCannedResponsesItem> Responses);

/// <summary>The unvalidated wire shape of one response - see <see cref="UpdateCannedResponses"/>.</summary>
public sealed record UpdateCannedResponsesItem(string Title, string Body);
