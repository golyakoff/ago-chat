using System.Security.Cryptography;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `System.Security.Cryptography.RandomNumberGenerator` (BCL, no new package) - the OS CSPRNG, the
/// identical primitive `WebhookSecretGenerator`/`OperatorInviteCodeGenerator` already use.
/// <see cref="RandomNumberGenerator.GetInt32(int, int)"/> rather than a hand-rolled modulo over
/// <see cref="RandomNumberGenerator.GetBytes(int)"/> - the BCL method already avoids modulo bias, which a
/// naive <c>bytes[0] % 1_000_000</c> would not (`IPendingChannelLinkCodeGenerator`'s own remarks on why
/// this value is short but still deserves an unbiased draw over its whole range).
/// </summary>
public sealed class PendingChannelLinkCodeGenerator : IPendingChannelLinkCodeGenerator
{
    // Six digits: 10^6 possibilities, small enough to type from memory into a chat window in a few
    // seconds, large enough that guessing one within a 15-minute window (PendingChannelLinkRequestOptions'
    // own default) is not a realistic attack on its own - see IPendingChannelLinkCodeGenerator's own
    // remarks on why the real bound here is scope and expiry, not code length.
    private const int Digits = 6;
    private const int UpperBoundExclusive = 1_000_000;

    public string NewCode() =>
        RandomNumberGenerator.GetInt32(0, UpperBoundExclusive).ToString($"D{Digits}");
}
