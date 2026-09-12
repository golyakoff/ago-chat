namespace Ago.Chat.Domain;

/// <summary>
/// An operation was attempted against a <see cref="VisitorContactDetail"/> in a state that cannot
/// legally perform it - the same shape <see cref="InvalidConversationStateException"/>'s own remarks
/// describe for a different aggregate. By the time this reaches <see cref="VisitorContactDetail"/>, the
/// Application layer has already validated the ordinary, expected case (`25-58`'s
/// <c>SetVisitorContactDetailAssessmentHandler</c> rejects a <see cref="VisitorContactDetailKind.Name"/>
/// row with a normal, user-facing <c>VisitorContactDetail.AssessmentNotApplicable</c> error before ever
/// calling <see cref="VisitorContactDetail.SetAssessment"/>) - reaching here at all means the caller's
/// own state was stale, a bug, never an expected user-facing outcome (coding-style.md). Domain still
/// guards its own invariant regardless, the same defence-in-depth every other aggregate's own
/// <c>Invalid*StateException</c> already practises.
/// </summary>
public sealed class InvalidVisitorContactDetailStateException(string message) : Exception(message);
