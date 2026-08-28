namespace Ago.Chat.Domain;

/// <summary>
/// `13-01`: a single-use, expiring, role-specific invitation to become a second (or third, ...)
/// `Operator` of a `Site` - the mechanism `10-02`'s own Out of scope flagged as unbuilt ("a real invite
/// flow is new scope no roadmap stage names yet"). Its own aggregate, not folded into `Site` or
/// `Operator` - an invite has its own lifecycle (generated, redeemed once, or left to expire unredeemed)
/// and its own transaction boundary at generation time, the same "does this change independently, in
/// its own transaction" test <see cref="WebhookEndpoint"/>'s own remarks apply to justify *its*
/// separation from <see cref="Domain.Site"/>. Redemption is the one exception - it commits atomically
/// with the `Operator`/`operator_roles` rows it produces, which is <c>IOperatorInviteRedemptionRepository</c>'s
/// job, not this aggregate's own (the same split <see cref="RegisterSiteHandler"/>'s remarks describe
/// for why that provisioning step is "one wider transaction" than the usual one-aggregate rule).
///
/// <para><see cref="CodeHash"/>, not a reversible ciphertext: unlike <see cref="WebhookEndpoint.SecretCiphertext"/>
/// (which `6-05`'s dispatcher must decrypt back to plaintext to sign a request), an invite code is only
/// ever *compared* at redemption, never reproduced afterward - the same one-way-hash reasoning
/// `adr/0024` used to *reject* hashing for its own reversible-secret case, applied correctly here
/// instead (see this item's own backlog note on the contrast).</para>
/// </summary>
public sealed class OperatorInvite
{
    public OperatorInviteId Id { get; }

    public SiteId SiteId { get; }

    /// <summary>The site's own `roles` row the invitee will hold once redeemed - `"Operator"` or
    /// `"Admin"`, resolved by name to this id at generation time (`CreateOperatorInviteHandler`). A
    /// plain `Guid`, not a Domain id type, matching `OperatorRoleRecord.RoleId`'s own shape - roles
    /// have no Domain/Application model of their own yet (`RoleRecord`'s own remarks: "nothing above
    /// `PermissionChecker` manages roles yet, so there is nothing for a richer model to buy").</summary>
    public Guid RoleId { get; }

    /// <summary>SHA-256 of the plaintext code shown to the caller exactly once, at generation
    /// (`CreateOperatorInviteHandler`) - never stored or logged in plaintext form anywhere.</summary>
    public byte[] CodeHash { get; } = [];

    public OperatorId CreatedByOperatorId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? RedeemedAt { get; private set; }

    public OperatorId? RedeemedByOperatorId { get; private set; }

    public bool IsRedeemed => RedeemedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    private OperatorInvite(
        OperatorInviteId id,
        SiteId siteId,
        Guid roleId,
        byte[] codeHash,
        OperatorId createdByOperatorId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? redeemedAt,
        OperatorId? redeemedByOperatorId)
    {
        Id = id;
        SiteId = siteId;
        RoleId = roleId;
        CodeHash = codeHash;
        CreatedByOperatorId = createdByOperatorId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RedeemedAt = redeemedAt;
        RedeemedByOperatorId = redeemedByOperatorId;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private OperatorInvite()
    {
    }

    public static OperatorInvite Generate(
        OperatorInviteId id,
        SiteId siteId,
        Guid roleId,
        byte[] codeHash,
        OperatorId createdByOperatorId,
        DateTimeOffset now,
        TimeSpan validFor) =>
        new(id, siteId, roleId, codeHash, createdByOperatorId, now, now + validFor, redeemedAt: null, redeemedByOperatorId: null);

    /// <summary>
    /// Marks this invite consumed by <paramref name="operatorId"/> - the new `Operator` row this
    /// redemption produced, in the same transaction as this save
    /// (`OperatorInviteRedemptionRepository`'s own remarks). Throws rather than silently no-opping on
    /// an already-redeemed or expired invite: `OperatorInviteRedemptionRepository` already checked both
    /// facts before calling this, so reaching the throw at all means a genuine race that check could
    /// not close by itself - the invite's own `xmin` optimistic-concurrency token
    /// (`OperatorInviteConfiguration`) is what actually stops two concurrent redemptions of the same
    /// code from both winning, and this guard is the last line of defence for a caller that skipped that
    /// pre-check entirely, matching <see cref="WebhookEndpoint.Revoke"/>'s own shape.
    ///
    /// <para><b>Deliberately does not check the site's seat limit</b> - that is a fact about the `Site`
    /// aggregate and its sibling `operators` rows, not about this invite, and checking it here would
    /// need this aggregate to reach outside itself for a live count `OperatorInviteRedemptionRepository`'s
    /// own row lock already owns (`docs/architecture/data-model.md`'s row-lock-vs-shadow-counter note).
    /// A capacity rejection is a `Result` the repository returns to its caller *before* ever calling this
    /// method, precisely so a capacity-rejected invite is never marked redeemed at all (this item's own
    /// Done-when: "a capacity-rejected invite is confirmed still redeemable afterward").</para>
    /// </summary>
    public void Redeem(OperatorId operatorId, DateTimeOffset now)
    {
        if (IsRedeemed)
        {
            throw new InvalidOperatorInviteStateException($"Operator invite {Id.Value} was already redeemed.");
        }

        if (IsExpired(now))
        {
            throw new InvalidOperatorInviteStateException($"Operator invite {Id.Value} has expired.");
        }

        RedeemedAt = now;
        RedeemedByOperatorId = operatorId;
    }
}
