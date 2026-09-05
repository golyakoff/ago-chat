using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UpdateAssignmentPenalty;

/// <summary>`23-05`. <see cref="PenaltySeconds"/> arrives unvalidated - a raw wire integer, not yet
/// checked for being positive - the same split <c>UpdateOfflineAutoReply</c>'s own remarks draw for
/// its raw rule strings: <see cref="UpdateAssignmentPenaltyHandler"/> is what turns a bad value into a
/// clean <c>Result</c> failure instead of an unhandled <c>ArgumentOutOfRangeException</c> surfacing
/// from <c>Site.UpdateAssignmentPenalty</c> itself.</summary>
public sealed record UpdateAssignmentPenalty(SiteId SiteId, OperatorId RequestedBy, int PenaltySeconds);
