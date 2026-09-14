namespace Ago.Chat.Domain;

/// <summary>
/// `25-04`: "this site has turned the AI add-on on, and from exactly this instant" - one row per site,
/// holding the <b>cut-off</b> that decision 6 requires ("only conversations from the moment of
/// enabling; the archive is not re-processed").
///
/// <para><b>Off by default for everyone, expressed structurally rather than by a default value.</b> A
/// site with no row at all is off, and a row is only ever written by
/// <c>EnableAiAddOnHandler</c>/<c>DisableAiAddOnHandler</c> - so "every tenant until they act" is not a
/// migration backfill that could be got wrong, it is the absence of a row (decision 2). The read side
/// (<c>IAiAddOnReadStore</c>) returns <see langword="null"/> for a site with no row and every caller
/// treats that as off, the identical "missing means nothing, not an error" shape
/// <see cref="ModuleQuantityGrant"/>'s own read port already establishes.</para>
///
/// <para><b>Keyed by <see cref="SiteId"/> alone, no synthetic id</b> - the same call
/// <see cref="ModuleQuantityGrant"/> made and for the same reason: there is exactly one enablement per
/// site, so the natural key <em>is</em> the identity and a synthetic id would add a second way for two
/// contradictory rows to exist.</para>
///
/// <para><b><see cref="EnabledAt"/> survives a disable, and <see cref="IsEnabled"/> is the separate
/// flag.</b> Collapsing the two (null-out the timestamp on disable) would make the row unable to answer
/// "when was this last on", which is the first question an audit asks after a tenant turns it off. It
/// also makes re-enabling honest: <see cref="Enable"/> moves the cut-off forward to the new instant, so
/// conversations created during the off period are never swept up by the re-enable - the conservative
/// direction, and the one decision 6's own reasoning points at ("reaching back would be exactly the
/// retroactive widening the instruction rules exist to prevent").</para>
///
/// <para><b>Why the cut-off compares against a conversation's <em>creation</em>, not its close.</b>
/// The agreement's own point 3 says «диалоги, созданные после него» - created after. That is also
/// strictly stronger than the item's Done-when phrasing ("a conversation closed before the cut-off is
/// never categorised"), because a conversation created at or after the cut-off necessarily closed at or
/// after it too. Choosing the weaker test would have let a conversation that ran for days before the
/// tenant ever saw the agreement be sent to a vendor merely because it happened to close afterwards.</para>
/// </summary>
public sealed class AiAddOnEnablement
{
    public const int MaxDocumentKeyLength = AcceptanceRecord.MaxDocumentKeyLength;
    public const int MaxDocumentVersionLength = AcceptanceRecord.MaxDocumentVersionLength;

    public SiteId SiteId { get; }

    public bool IsEnabled { get; private set; }

    /// <summary>The instant of the most recent <see cref="Enable"/> - the cut-off. Never cleared, see
    /// this type's own remarks.</summary>
    public DateTimeOffset? EnabledAt { get; private set; }

    public OperatorId? EnabledBy { get; private set; }

    /// <summary>The document key and version the enabling operator had accepted at the moment they
    /// enabled - a <em>copy</em> for legibility, never the authoritative record. The authority is the
    /// `24-01` <see cref="AcceptanceRecord"/> row, which is immutable and carries its own timestamp and
    /// subject; this pair exists so a support conversation can answer "which version is this site
    /// running under" without a second query, and <c>EnableAiAddOnHandler</c> is the only writer.</summary>
    public string? AcceptedDocumentKey { get; private set; }

    public string? AcceptedDocumentVersion { get; private set; }

    public DateTimeOffset? DisabledAt { get; private set; }

    public OperatorId? DisabledBy { get; private set; }

    /// <summary>The cut-off currently in force, or <see langword="null"/> when the add-on is off - the
    /// one value every reader acts on, and the only thing this aggregate exposes about eligibility.
    ///
    /// <para><b>The comparison itself deliberately does <em>not</em> live here.</b> A <c>Covers(createdAt)</c>
    /// method on this type would have read well and been dead code: neither AI path ever holds this
    /// aggregate. Both reach the fact through <see cref="Application.Abstractions.IAiAddOnReadStore"/>
    /// (raw SQL, no change tracker - `adr/0004`), so the comparison lives in
    /// <c>Application.UseCases.AiAddOn.AiProcessingGate</c>, the one place that has both the cut-off and
    /// the conversation. What this type owns is the harder half - that a disabled row yields no cut-off
    /// at all, so a caller cannot compare against a stale one.</para></summary>
    public DateTimeOffset? EffectiveFrom => IsEnabled ? EnabledAt : null;

    private AiAddOnEnablement(SiteId siteId)
    {
        if (siteId.Value == Guid.Empty)
        {
            throw new ArgumentException("A site id cannot be empty.", nameof(siteId));
        }

        SiteId = siteId;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private AiAddOnEnablement()
    {
    }

    /// <summary>A site that has never touched the setting - off, with no cut-off. Created by
    /// <c>EnableAiAddOnHandler</c> only when it is about to enable; nothing writes a row that stays
    /// off, so "no row" and "row that says off" mean the same thing to every reader.</summary>
    public static AiAddOnEnablement ForSite(SiteId siteId) => new(siteId);

    public void Enable(OperatorId enabledBy, string documentKey, string documentVersion, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(documentKey))
        {
            throw new ArgumentException("An accepted document key cannot be empty.", nameof(documentKey));
        }

        if (string.IsNullOrWhiteSpace(documentVersion))
        {
            throw new ArgumentException("An accepted document version cannot be empty.", nameof(documentVersion));
        }

        IsEnabled = true;
        EnabledAt = now;
        EnabledBy = enabledBy;
        AcceptedDocumentKey = documentKey.Trim();
        AcceptedDocumentVersion = documentVersion.Trim();
    }

    public void Disable(OperatorId disabledBy, DateTimeOffset now)
    {
        IsEnabled = false;
        DisabledAt = now;
        DisabledBy = disabledBy;
    }
}
