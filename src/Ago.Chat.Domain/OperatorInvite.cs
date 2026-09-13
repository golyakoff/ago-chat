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

    /// <summary>`25-73`: the invitee's own address - required since this item, because Keycloak's own
    /// invite primitive (admin-created-user + `execute-actions-email`) is what actually delivers this
    /// invite now, replacing the admin copying a link themselves. Not a replacement for
    /// <see cref="CodeHash"/> - one email can legitimately hold invites to more than one site at once
    /// (an agency operator invited to several shops), so the code is still what disambiguates *which*
    /// invite a redemption is claiming; the email is the second half of the same security boundary
    /// (`RedeemOperatorInviteHandler`'s own remarks: "the code says which invite, the Keycloak session
    /// says who is actually claiming it, and both must agree").</summary>
    public string Email { get; } = string.Empty;

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

    /// <summary>`25-73`: when an admin revoked this invite before it was ever redeemed
    /// (<see cref="Revoke"/>) - <see langword="null"/> for every invite nobody has revoked, the same
    /// "absence is the ordinary case" reading <see cref="Site.SuspendedUntil"/>'s own remarks give an
    /// optional instant elsewhere in this codebase.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    /// <summary>`25-73`: the SMTP-layer error Keycloak's own realm relay reported back for this
    /// invite's `execute-actions-email` call, or <see langword="null"/> if it was sent without one (or
    /// has not been attempted). Recorded so the console's own invite-list screen can show the real
    /// failure rather than swallowing it into a log line (this item's own point 6) - never retried from
    /// here, and never cleared once set: a resend is out of this item's own scope
    /// ("no reminder/nudge mechanism is being built here").</summary>
    public string? SendFailureCode { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    private OperatorInvite(
        OperatorInviteId id,
        SiteId siteId,
        Guid roleId,
        byte[] codeHash,
        string email,
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
        Email = email;
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
        string email,
        OperatorId createdByOperatorId,
        DateTimeOffset now,
        TimeSpan validFor)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            // `25-73`: a required field since this item - CreateOperatorInviteHandler is expected to
            // validate the shape of the address before ever reaching this factory (the same
            // "validate at the Application boundary, this constructor's only job is applying it" split
            // Site's own update methods already draw), so reaching this is a caller bug, not a
            // reportable business refusal - thrown, the same shape Site's constructor already uses for
            // its own required PublicKey.
            throw new ArgumentException("Operator invite email cannot be empty.", nameof(email));
        }

        return new(id, siteId, roleId, codeHash, email, createdByOperatorId, now, now + validFor, redeemedAt: null, redeemedByOperatorId: null);
    }

    /// <summary>`25-73`: an admin's own decision to withdraw an unredeemed invite - `OperatorsTeamPage`'s
    /// new "отозвать" button, the console's own read-modify-write through <c>RevokeOperatorInviteHandler</c>.
    /// Throws on an already-redeemed or already-revoked invite, the identical "the repository already
    /// checked both facts before calling this; reaching the throw at all means a genuine race" shape
    /// <see cref="Redeem"/>'s own remarks describe for itself - <see cref="RevokeOperatorInviteHandler"/>
    /// (Application) is expected to have already checked <see cref="IsRedeemed"/>/<see cref="IsRevoked"/>
    /// against a value this same load read, so this guard is the last line of defence, not the primary
    /// check.</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (IsRedeemed)
        {
            throw new InvalidOperatorInviteStateException($"Operator invite {Id.Value} was already redeemed.");
        }

        if (IsRevoked)
        {
            throw new InvalidOperatorInviteStateException($"Operator invite {Id.Value} was already revoked.");
        }

        RevokedAt = now;
    }

    /// <summary>`25-73`: records that Keycloak's own realm relay failed to deliver this invite's
    /// `execute-actions-email` at the SMTP layer - called by <c>CreateOperatorInviteHandler</c> in the
    /// same request that generated this invite, immediately before the first save, so the failure and
    /// the row it describes reach the database together rather than as a later update. Deliberately no
    /// domain event: the one consumer that would ever read this is the console's own invite-list
    /// screen, an operator-authenticated, uncached read straight from the database - the identical
    /// "no propagation delay for an event to solve" reasoning <see cref="Site.UpdateCannedResponses"/>'s
    /// own remarks give for its own no-event write.</summary>
    public void MarkSendFailed(string smtpErrorCode)
    {
        if (string.IsNullOrWhiteSpace(smtpErrorCode))
        {
            throw new ArgumentException("SMTP error code cannot be empty.", nameof(smtpErrorCode));
        }

        SendFailureCode = smtpErrorCode;
    }

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

        // `25-73`: the third terminal, static fact `OperatorInviteRedemptionRepository` checks before
        // ever taking its row lock - the same "a caller that skipped that pre-check entirely" backstop
        // this method's own remarks already describe for the two checks above.
        if (IsRevoked)
        {
            throw new InvalidOperatorInviteStateException($"Operator invite {Id.Value} was revoked.");
        }

        RedeemedAt = now;
        RedeemedByOperatorId = operatorId;
    }
}
