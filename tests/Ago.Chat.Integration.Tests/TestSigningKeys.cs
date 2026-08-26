using System.Security.Cryptography;
using Ago.Chat.Api.Auth;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `17-03`: the one-line way for a test that only needs *a* visitor token to get a key ring, so that
/// <see cref="JwtTokenService"/>'s new dependency does not paste six lines of key construction into
/// every file that mints one. Tests that are *about* rotation build their own multi-key rings
/// (<see cref="VisitorSigningKeyRingTests"/>, <see cref="VisitorKeyRotationTests"/>) - this helper
/// deliberately only produces the trivial single-active-key case.
/// </summary>
internal static class TestSigningKeys
{
    /// <summary>A fresh 32-byte key, base64 as configuration carries it.</summary>
    public static string NewKeyValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>A ring holding one active key nothing else knows.</summary>
    public static VisitorSigningKeyRing Ring(IClock? clock = null) => Ring(NewKeyValue(), clock);

    /// <summary>A ring holding one active key with a caller-chosen value, for a test that also has to
    /// validate what it minted.</summary>
    public static VisitorSigningKeyRing Ring(string activeKeyValue, IClock? clock = null) => new(
        new VisitorSigningKeyOptions
        {
            Keys = { new VisitorSigningKeyEntry { Id = "test-active", Value = activeKeyValue } },
        },
        clock ?? new SystemClock());
}
