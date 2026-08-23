using System.Security.Cryptography;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `System.Security.Cryptography.RandomNumberGenerator` (BCL, no new package) - the OS CSPRNG, the
/// same primitive `Program.cs` already uses for the JWT signing-key fallback. 256 bits: enough entropy
/// that brute force is infeasible regardless of how fast or slow the hash/cipher protecting it at rest
/// is (see `adr/00XX`'s reasoning for why that also settles the hash-vs-encrypt question).
/// </summary>
public sealed class WebhookSecretGenerator : IWebhookSecretGenerator
{
    private const string Prefix = "whsec_";
    private const int SecretBytesLength = 32;

    public string NewSecret() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytesLength))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
