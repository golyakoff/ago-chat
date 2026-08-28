using System.Security.Cryptography;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `System.Security.Cryptography.RandomNumberGenerator` (BCL, no new package) - the OS CSPRNG, the
/// identical primitive `WebhookSecretGenerator` already uses. Same entropy (256 bits: brute force is
/// infeasible regardless of how fast or slow the hash protecting it at rest is, `adr/0024`'s own
/// reasoning) and the same base64url encoding - only the prefix differs, because this value is shown
/// directly to a person (`IOperatorInviteCodeGenerator`'s own remarks on why this is not simply a
/// second call site for `WebhookSecretGenerator`).
/// </summary>
public sealed class OperatorInviteCodeGenerator : IOperatorInviteCodeGenerator
{
    private const string Prefix = "invite_";
    private const int CodeBytesLength = 32;

    public string NewCode() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(CodeBytesLength))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
