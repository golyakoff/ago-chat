using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RedeemOperatorInvite;

/// <summary><paramref name="ExternalSubjectId"/> comes from the validated token's `sub` claim, never
/// the request body - the same "identity comes from the validated token, not user-supplied data" rule
/// `10-02`'s own `RegisterSite` command already established for the bootstrap endpoint this one
/// mirrors.</summary>
public sealed record RedeemOperatorInvite(string ExternalSubjectId, string Code);

public sealed record RedeemedOperatorInvite(OperatorId OperatorId, SiteId SiteId);
