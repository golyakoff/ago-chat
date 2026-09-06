namespace Ago.Chat.Domain;

/// <summary>
/// `23-11`/`decisions.md` §5: how a tenant's contact surfaces show a visitor's phone/email to the
/// operators who read them - one setting on the account (<see cref="Site.ContactVisibility"/>), not
/// one per product, so a tenant sold "your staff cannot casually copy the customer list" gets that
/// answer for every store this system holds one in, not only the store whichever item shipped first.
///
/// <para><b>Only two members. Rung three ("Never") is deliberately absent - not a value nobody sets,
/// not a value gated behind a flag, absent from this enum entirely.</b> §5 is explicit that the third
/// rung must not be sold until the system can place the call itself (click-to-call, system-sent
/// messages) - without that, "never visible" is not protection, it is an operator who cannot do the
/// job. A value present in the code, even one nothing ever constructs, is a value a future console
/// screen or a future support conversation can point at and offer - the same "no domain-event
/// plumbing ahead of a real subscriber" discipline this codebase already applies elsewhere
/// (<see cref="WebhookDelivery"/>'s own remarks), applied here to an enum member instead of a method.
/// Adding it later is one more line in this file and a widened `CHECK` constraint - a small, honest
/// cost for keeping an unbuilt guarantee out of the type system until it is real.</para>
/// </summary>
public enum ContactVisibility
{
    /// <summary>Everything visible - today's behaviour, and the default for every tenant that never
    /// touches this setting (micro case: the person reading the number *is* the tenant, and masking
    /// their own view of their own customer would protect nobody).</summary>
    Visible,

    /// <summary>Masked in every list read; a deliberate reveal returns the value and leaves a record
    /// naming who asked. §5's own framing: attribution, not prevention - most exfiltration is casual,
    /// and a log stops casual.</summary>
    MaskedWithReveal,
}
