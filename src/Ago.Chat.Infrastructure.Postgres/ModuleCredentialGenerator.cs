using System.Security.Cryptography;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `System.Security.Cryptography.RandomNumberGenerator` (BCL, no new package) - the OS CSPRNG, the
/// identical primitive <see cref="WebhookSecretGenerator"/>'s own remarks give for its sibling. 32
/// random bytes, base64url-encoded without padding: comfortably inside
/// <see cref="Domain.ModuleCredential"/>'s own <c>MinLength</c>/<c>MaxLength</c> bounds and carrying
/// 256 bits of entropy, the same floor <see cref="WebhookSecretGenerator"/>'s own remarks argue for.
/// </summary>
public sealed class ModuleCredentialGenerator : IModuleCredentialGenerator
{
    private const int SecretBytesLength = 32;

    public string NewCredential() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytesLength))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
