using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RedeemPendingOperatorInviteForCaller;

/// <summary>`25-85`: the "activate it here" card's own command - <paramref name="ExternalSubjectId"/>/
/// <paramref name="Email"/>/<paramref name="Name"/> all come from the caller's own validated token
/// claims, the identical "identity comes from the validated token, never the request body" rule
/// `RedeemOperatorInvite`'s own remarks already state for the code-based redemption this command sits
/// beside - there is no request body at all here, since there is no code to carry in one.</summary>
public sealed record RedeemPendingOperatorInviteForCaller(string ExternalSubjectId, string Email, string? Name = null);

public sealed record RedeemedPendingOperatorInvite(OperatorId OperatorId, SiteId SiteId);
