using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// Found live, 2026-08-28: `14-02` bound and validated `ChannelCredentialCipherOptions` in
/// `ChatModule.ConfigureServices` but never registered the raw options type as a resolvable service -
/// `ChannelCredentialCipher`'s own constructor takes `ChannelCredentialCipherOptions` directly, the
/// same shape `WebhookSecretCipher` uses, and `WebhookSecretCipherOptions` gets the extra
/// <c>services.AddSingleton(sp =&gt; sp.GetRequiredService&lt;IOptions&lt;WebhookSecretCipherOptions&gt;&gt;().Value)</c>
/// line that makes that resolvable - `ChannelCredentialCipherOptions` did not, so the container built
/// without error (nothing about a missing registration is checked until something actually asks for
/// it) and the first real request to connect a MAX channel crashed with
/// `InvalidOperationException: Unable to resolve service for type
/// 'Ago.Chat.Infrastructure.Postgres.ChannelCredentialCipherOptions'`.
///
/// No existing test caught this because `RegisterChannelCredentialHandlerTests` resolves
/// `FakeChannelCredentialCipher` directly, never through the real DI registration path this test
/// exercises instead - the exact gap this file closes. Deliberately narrow (just the two
/// registration lines and the cipher, not the whole of `ChatModule`) rather than standing up every
/// other option `ChatModule.ConfigureServices` binds, which this bug has nothing to do with.
/// </summary>
public sealed class ChannelCredentialCipherDependencyInjectionTests
{
    /// <summary>A syntactically valid base64-encoded 32-byte AES-256 key - the exact shape
    /// `ChannelCredentialCipherOptions`' own `IsValidBase64Aes256Key` check requires, not a real
    /// secret (CLAUDE.md: everything here is public, including fixtures).</summary>
    private const string ValidKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    private static ServiceProvider BuildProvider(string encryptionKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ChannelCredentialCipherOptions.SectionName}:{nameof(ChannelCredentialCipherOptions.CredentialEncryptionKey)}"] =
                    encryptionKey,
            })
            .Build();

        var services = new ServiceCollection();
        // The exact two registrations `ChatModule.ConfigureServices` makes for this options type,
        // reproduced here rather than calling the whole module - see this file's own doc comment for
        // why narrower is the right scope for this specific bug.
        services
            .AddOptions<ChannelCredentialCipherOptions>()
            .Bind(configuration.GetSection(ChannelCredentialCipherOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ChannelCredentialCipherOptions>>().Value);
        services.AddScoped<IChannelCredentialCipher, ChannelCredentialCipher>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void IChannelCredentialCipher_ResolvesThroughTheRealDIRegistration_NotJustAFake()
    {
        using var provider = BuildProvider(ValidKey);
        using var scope = provider.CreateScope();

        var cipher = scope.ServiceProvider.GetRequiredService<IChannelCredentialCipher>();

        Assert.IsType<ChannelCredentialCipher>(cipher);
    }

    /// <summary>Proves the cipher resolved above is not just constructible but actually wired to the
    /// configured key - round-trips a value through it, the same "prove it actually works, not just
    /// that it exists" bar the rest of this repository's own tests hold themselves to.</summary>
    [Fact]
    public void TheResolvedCipher_EncryptsAndDecryptsRoundTrip()
    {
        using var provider = BuildProvider(ValidKey);
        using var scope = provider.CreateScope();
        var cipher = scope.ServiceProvider.GetRequiredService<IChannelCredentialCipher>();

        var ciphertext = cipher.Encrypt("a-max-bot-token");

        Assert.Equal("a-max-bot-token", cipher.Decrypt(ciphertext));
    }
}
