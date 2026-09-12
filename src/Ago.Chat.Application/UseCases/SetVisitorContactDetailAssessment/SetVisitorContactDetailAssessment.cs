using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetVisitorContactDetailAssessment;

/// <summary>
/// `25-58`: an operator confirms or marks invalid an existing <see cref="VisitorContactDetailKind.Phone"/>
/// or <see cref="VisitorContactDetailKind.Email"/> row - never a <see cref="VisitorContactDetailKind.Name"/>
/// one (<see cref="SetVisitorContactDetailAssessmentHandler"/>'s own remarks on where that is rejected).
/// <paramref name="Assessment"/> arrives as a raw wire string, not yet the validated
/// <see cref="VisitorContactDetailAssessment"/> - the identical "the command carries the raw value, the
/// handler translates it" split <see cref="RecordVisitorContactDetail.RecordVisitorContactDetailAsOperator"/>
/// already draws for <c>Kind</c>.
/// </summary>
public sealed record SetVisitorContactDetailAssessment(
    OperatorId RequestedBy, SiteId SiteId, ConversationId ConversationId, VisitorContactDetailId ContactDetailId,
    string Assessment);
