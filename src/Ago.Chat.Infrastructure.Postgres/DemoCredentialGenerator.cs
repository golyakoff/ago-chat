using System.Security.Cryptography;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `8-07`: implements <see cref="IDemoCredentialGenerator"/> with the platform's CSPRNG, the same
/// source <c>WebhookSecretGenerator</c> uses.
///
/// <para>In <c>Infrastructure.Postgres</c> despite having nothing to do with Postgres, following
/// <c>WebhookSecretGenerator</c>'s own precedent in this same folder rather than creating a
/// <c>Ago.Chat.Infrastructure.Crypto</c> project for two small classes.
/// <c>clean-architecture.md</c>'s "one project per external technology" is about things that can be
/// swapped; the BCL's random number generator is not one of them.</para>
/// </summary>
public sealed class DemoCredentialGenerator : IDemoCredentialGenerator
{
    // No look-alikes: 0/O, 1/l/I are absent. Somebody is reading this off a screen and typing it into
    // a login form, and a password that is technically strong and practically mistypeable wastes the
    // one interaction this whole item exists to make smooth.
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzACDEFGHJKLMNPQRSTUVWXYZ23456789";

    // 14 characters over a 55-character alphabet is ~81 bits. Far beyond what a 24-hour tenant holding
    // no real data needs, and still short enough to type. Not a measured figure - a deliberate margin.
    private const int Length = 14;

    public string NewPassword() => RandomNumberGenerator.GetString(Alphabet, Length);

    // Eight characters of the same alphabet is ~46 bits - collision-free in practice for a population
    // this endpoint's own cap keeps below a hundred, and still short enough to read aloud.
    public string NewUsernameSuffix() => RandomNumberGenerator.GetString(Alphabet, 8);
}
