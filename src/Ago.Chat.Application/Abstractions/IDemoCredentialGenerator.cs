namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `8-07`: the password shown to a viewer once and never stored.
///
/// <para>A port because randomness is an infrastructure concern - Application may not reach for
/// <c>RandomNumberGenerator</c> any more than it may reach for <c>Guid.NewGuid()</c> (CLAUDE.md rule
/// 2), and a handler whose output cannot be pinned in a test is a handler whose output cannot be
/// asserted. The same shape <see cref="IWebhookSecretGenerator"/> already established for `6-03`'s
/// signing secrets.</para>
///
/// <para>Not a reuse of that port under a wider name: its contract is a webhook signing secret, and a
/// human has to read this one off a screen and type it. The two have different shapes for a real
/// reason, and one interface serving both would have to lie about at least one.</para>
/// </summary>
public interface IDemoCredentialGenerator
{
    /// <summary>A password with enough entropy to be unguessable and few enough characters to be
    /// typed. It protects a tenant that holds no real data and dies within a day, which is why this is
    /// a readable string rather than the 32 bytes a signing secret gets.</summary>
    string NewPassword();

    /// <summary>
    /// The random part of a minted username.
    ///
    /// <para><b>This exists because the obvious alternative is broken, and a test found it.</b> The
    /// username was first derived from the new operator's UUIDv7 - `demo-` plus its first eight hex
    /// characters. UUIDv7's leading bits <em>are</em> the millisecond timestamp, so those eight
    /// characters change roughly once a minute and every mint inside the same window produced the same
    /// username; Keycloak answered `409 Conflict` for the second one onward
    /// (`DemoTenantLifecycleTests.TwoMintsAreTwoTenantsWithNothingShared`, which failed exactly that
    /// way). A time-ordered id is the right thing for a primary key and the wrong thing for anything
    /// that has to be unique <em>and</em> short.</para>
    /// </summary>
    string NewUsernameSuffix();
}
