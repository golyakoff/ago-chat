using System.Security.Cryptography;
using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `adr/0089`: derives the signed 64-bit key `pg_try_advisory_lock`/`pg_advisory_unlock` require from
/// a <see cref="ChannelCredentialId"/>'s UUID. SHA-256 over the id's raw bytes, first 8 bytes read back
/// as a <see cref="long"/> - deterministic across processes and runs, unlike
/// <see cref="object.GetHashCode"/>, which .NET explicitly randomises per process and would make two
/// Worker instances compute two different keys for the identical credential, defeating the whole
/// mechanism silently rather than loudly. The same "pure, deterministic, no I/O, no port needed"
/// primitive <c>ExternalMessageId.ToClientMessageId</c> already uses a bare <c>SHA256</c> call for in
/// <c>Ago.Chat.Domain</c>; this one stays in <c>Ago.Chat.Infrastructure.Postgres</c> rather than Domain
/// because an advisory-lock key is a PostgreSQL concept with no meaning outside this adapter -
/// `adr/0089`'s own placement reasoning for the whole mechanism, applied to this one pure function too.
///
/// <para><b>A collision here means one bot silently never polls</b> (`adr/0089`'s own named negative
/// consequence) - two different <see cref="ChannelCredentialId"/>s hashing to the same key make
/// PostgreSQL treat them as one lock, so whichever credential's process acquires it first blocks the
/// other's forever, indistinguishable from an ordinary contested acquire. Negligible at 64 bits and
/// this system's scale, but not zero - <see cref="PostgresChannelPollerOwnership"/>'s own remarks
/// describe how this is made observable rather than left merely improbable.</para>
/// </summary>
public static class AdvisoryLockKey
{
    public static long For(ChannelCredentialId credentialId)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(credentialId.Value.ToByteArray(), digest);
        return BitConverter.ToInt64(digest[..8]);
    }
}
