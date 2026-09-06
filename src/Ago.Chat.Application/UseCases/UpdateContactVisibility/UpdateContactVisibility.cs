using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UpdateContactVisibility;

/// <summary>`23-11`. <see cref="Rung"/> arrives unvalidated - a raw wire string, not yet checked
/// against <see cref="ContactVisibility"/>'s own defined members - the same split
/// `UpdateAssignmentPenalty`'s own remarks draw for its raw integer:
/// <see cref="UpdateContactVisibilityHandler"/> is what turns an unrecognised or rung-three value into
/// a clean <c>Result</c> failure.</summary>
public sealed record UpdateContactVisibility(SiteId SiteId, OperatorId RequestedBy, string Rung);
