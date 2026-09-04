using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RedeemOperatorInvite;

/// <summary><paramref name="ExternalSubjectId"/> comes from the validated token's `sub` claim, never
/// the request body - the same "identity comes from the validated token, not user-supplied data" rule
/// `10-02`'s own `RegisterSite` command already established for the bootstrap endpoint this one
/// mirrors.
///
/// <para>`23-02`: <paramref name="Name"/>/<paramref name="Email"/> come from that same token's `name`/
/// `email` claims - "capture at redemption" (`decisions.md` §1), the one path that creates an
/// `Operator` from a real human's token. Optional, the same "nothing to invent if it is not there"
/// shape every other claim-derived field in this codebase already uses.</para></summary>
public sealed record RedeemOperatorInvite(string ExternalSubjectId, string Code, string? Name = null, string? Email = null);

public sealed record RedeemedOperatorInvite(OperatorId OperatorId, SiteId SiteId);
