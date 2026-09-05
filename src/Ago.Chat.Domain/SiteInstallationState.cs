namespace Ago.Chat.Domain;

/// <summary>
/// `23-06`: the one of four headline states a tenant's install screen leads with -
/// <see cref="SiteInstallationStateResolver"/> is what picks one from the raw facts. Every member
/// name matches the Goal's own four-way split in `docs/backlog/23-06-*.md` word for word, so a
/// reviewer can check this enum against that prose without a translation step.
/// </summary>
public enum SiteInstallationState
{
    /// <summary>The widget has never been seen, and nothing suggests the product is in use another
    /// way either. The ordinary state for a brand-new tenant on day one - <c>decisions.md</c> §3's
    /// "three zeros are the normal first state", restated for this item's own two facts.</summary>
    NotSeenYet,

    /// <summary>The widget has been seen at least once, and there is no more recent refusal than the
    /// most recent sighting - "installed", worded by <c>FirstSeenAt</c>/<c>LastSeenAt</c> as how long
    /// it has been quiet.</summary>
    SeenAndQuiet,

    /// <summary>A refused origin was recorded, and it is not older than the most recent successful
    /// sighting (or there has never been one) - the `www.` vs. bare-domain, `http` vs. `https` case
    /// `decisions.md` §3 names by name: the widget is running and every one of its requests is being
    /// turned away.</summary>
    EveryRequestRefused,

    /// <summary>The widget has never been seen, but a conversation exists for this site within the
    /// configured recency window - `docs/backlog/23-06-*.md`'s "the last of which is an ordinary state
    /// for a tenant whose customers arrive over a channel." Must never be told "the script has not
    /// arrived yet" the way <see cref="NotSeenYet"/> is - that message would be actively wrong for
    /// this tenant.</summary>
    NeverSeenButInUse,
}
