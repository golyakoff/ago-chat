namespace Ago.Chat.Domain;

/// <summary>
/// `23-06`: resolves the four raw facts a tenant's install screen is built from into one
/// <see cref="SiteInstallationState"/>. Pure, no I/O - the same "belongs in Domain because nothing
/// here touches a database or a clock" reasoning <see cref="ChoiceReplyTextResolver"/> already states
/// for itself; every argument here is a value the caller (<c>GetSiteInstallationHandler</c>) already
/// read out of Postgres or computed against <c>IClock</c>, and this function only ever reasons about
/// values it was handed.
///
/// <para><b>Two facts, not one - `decisions.md` §3's own amendment, and the reason this is not a
/// three-branch <c>if</c> chain over <see cref="SiteInstallationState.NotSeenYet"/>/
/// <see cref="SiteInstallationState.SeenAndQuiet"/>/<see cref="SiteInstallationState.NeverSeenButInUse"/>
/// alone.</b> <paramref name="usedRecently"/> answers "is the product being used", which is
/// independent of whether the widget itself has ever connected - a channel-only tenant can be true on
/// one and false on the other, and collapsing them into a single reading is exactly the harm the
/// amendment forbids (a tenant whose customers arrive by SMS or Telegram must never be told "the
/// script has not arrived yet").</para>
///
/// <para><b>Why a refusal can win even over a tenant currently in use.</b> <paramref
/// name="lastRefusedOrigin"/> is a concrete, actionable finding - a specific origin being turned away -
/// and it takes priority over <see cref="SiteInstallationState.NeverSeenButInUse"/> whenever the widget
/// has never actually connected. A channel-only tenant who also has a broken embed still benefits more
/// from being told "your widget's origin is wrong, here is what to fix" than from a bare "not seen, but
/// that's fine" that hides an install attempt already failing on their site.</para>
///
/// <para><b>Why a refusal loses to a later success.</b> The check is not merely "has a refusal ever
/// happened" - it is "is the most recent refusal newer than the most recent success". A site that once
/// had a `www.` mismatch and has since been fixed keeps its old <paramref name="lastRefusedOrigin"/>
/// (this item's own Out of scope: no history, one value plus its timestamp), and reporting it as
/// currently broken would be exactly the stale, discouraging answer `decisions.md` §3 calls the "wrong
/// one".</para>
/// </summary>
public static class SiteInstallationStateResolver
{
    /// <summary><paramref name="firstSeenAt"/> is not read here - it drives no branch of this
    /// resolution, only the console's own "how long" wording once a state is chosen - and is not part
    /// of this function's signature at all, rather than an unused parameter merely accepted for
    /// symmetry with the DTO that carries it.</summary>
    public static SiteInstallationState Resolve(
        DateTimeOffset? lastSeenAt,
        string? lastRefusedOrigin,
        DateTimeOffset? lastRefusedOriginAt,
        bool usedRecently)
    {
        var isCurrentlyRefused = lastRefusedOrigin is not null
            && (lastSeenAt is null || (lastRefusedOriginAt is not null && lastRefusedOriginAt > lastSeenAt));

        if (isCurrentlyRefused)
        {
            return SiteInstallationState.EveryRequestRefused;
        }

        if (lastSeenAt is not null)
        {
            return SiteInstallationState.SeenAndQuiet;
        }

        return usedRecently ? SiteInstallationState.NeverSeenButInUse : SiteInstallationState.NotSeenYet;
    }
}
