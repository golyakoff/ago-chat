namespace Ago.Chat.Domain.Tests;

public class ChannelCredentialTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static byte[] Hash(string secret) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    private static ChannelCredential Register(string webhookSecret = "correct-secret") =>
        ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), SiteId, ChannelKind.Max, [1, 2, 3], Hash(webhookSecret), Now);

    [Fact]
    public void Register_StartsActive()
    {
        var credential = Register();

        Assert.True(credential.Active);
    }

    [Fact]
    public void Revoke_FlipsActiveToFalse()
    {
        var credential = Register();

        credential.Revoke();

        Assert.False(credential.Active);
    }

    [Fact]
    public void Revoke_WhenAlreadyRevoked_Throws()
    {
        var credential = Register();
        credential.Revoke();

        Assert.Throws<InvalidChannelCredentialStateException>(() => credential.Revoke());
    }

    [Fact]
    public void MatchesWebhookSecret_WithTheOriginalSecret_ReturnsTrue()
    {
        var credential = Register("correct-secret");

        Assert.True(credential.MatchesWebhookSecret("correct-secret"));
    }

    [Fact]
    public void MatchesWebhookSecret_WithAWrongSecret_ReturnsFalse()
    {
        var credential = Register("correct-secret");

        Assert.False(credential.MatchesWebhookSecret("wrong-secret"));
    }

    [Fact]
    public void MatchesWebhookSecret_WithEmptyCandidate_ReturnsFalse()
    {
        var credential = Register("correct-secret");

        Assert.False(credential.MatchesWebhookSecret(string.Empty));
    }

    /// <summary>`14-08`: MAX's/Telegram's own registrations never pass this parameter - the default
    /// keeps their own call sites (and this file's own <see cref="Register"/> helper above) unchanged,
    /// exactly as <see cref="Domain.ChannelCredential.Register"/>'s own remarks intend.</summary>
    [Fact]
    public void Register_WithNoProviderAccountId_LeavesItNull()
    {
        var credential = Register();

        Assert.Null(credential.ProviderAccountId);
    }

    /// <summary>VK's own registration path (`Ago.Chat.Api`'s <c>VkChannelEndpoints</c>) always supplies
    /// one - <see cref="Domain.ChannelCredential.ProviderAccountId"/>'s own remarks on why VK is the
    /// first channel that needs it.</summary>
    [Fact]
    public void Register_WithAProviderAccountId_StoresIt()
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), SiteId, ChannelKind.Vk, [1, 2, 3], Hash("s"), Now,
            providerAccountId: "987654");

        Assert.Equal("987654", credential.ProviderAccountId);
    }

    /// <summary>`14-11`: MAX's/Telegram's/VK's/WhatsApp's own registrations never pass this parameter -
    /// the default keeps their own call sites unchanged, the identical
    /// <see cref="Register_WithNoProviderAccountId_LeavesItNull"/> precedent for the second new
    /// parameter this item adds.</summary>
    [Fact]
    public void Register_WithNoRefreshTokenCiphertext_LeavesItNull()
    {
        var credential = Register();

        Assert.Null(credential.RefreshTokenCiphertext);
    }

    /// <summary>Avito's own registration path (`Ago.Chat.Api`'s <c>AvitoChannelEndpoints</c>) always
    /// supplies one - <see cref="Domain.ChannelCredential.RefreshTokenCiphertext"/>'s own remarks on why
    /// Avito is the first channel that needs it.</summary>
    [Fact]
    public void Register_WithARefreshTokenCiphertext_StoresIt()
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), SiteId, ChannelKind.Avito, [1, 2, 3], Hash("s"), Now,
            providerAccountId: "94235311", refreshTokenCiphertext: [9, 9, 9]);

        Assert.Equal(new byte[] { 9, 9, 9 }, credential.RefreshTokenCiphertext);
    }

    [Fact]
    public void RotateOAuthTokens_ReplacesBothTheTokenAndTheRefreshToken()
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), SiteId, ChannelKind.Avito, [1, 2, 3], Hash("s"), Now,
            providerAccountId: "94235311", refreshTokenCiphertext: [9, 9, 9]);

        credential.RotateOAuthTokens([7, 7, 7], [8, 8, 8]);

        Assert.Equal(new byte[] { 7, 7, 7 }, credential.TokenCiphertext);
        Assert.Equal(new byte[] { 8, 8, 8 }, credential.RefreshTokenCiphertext);
    }

    [Fact]
    public void RotateOAuthTokens_WhenTheCredentialWasNeverRegisteredWithARefreshToken_Throws()
    {
        var credential = Register();

        Assert.Throws<InvalidOperationException>(() => credential.RotateOAuthTokens([7, 7, 7], [8, 8, 8]));
    }

    [Fact]
    public void RotateOAuthTokens_WhenTheCredentialIsRevoked_Throws()
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), SiteId, ChannelKind.Avito, [1, 2, 3], Hash("s"), Now,
            providerAccountId: "94235311", refreshTokenCiphertext: [9, 9, 9]);
        credential.Revoke();

        Assert.Throws<InvalidChannelCredentialStateException>(() => credential.RotateOAuthTokens([7, 7, 7], [8, 8, 8]));
    }

    /// <summary>`23-85`/`adr/0151`: the system's own disconnect of a lapsed-entitlement account -
    /// <see cref="ChannelCredential.RevokeForLapsedEntitlement"/>'s own remarks on why "cleaned up"
    /// means more than <see cref="ChannelCredential.Revoke"/>'s own "flips Active to false."</summary>
    [Fact]
    public void RevokeForLapsedEntitlement_FlipsActiveToFalse()
    {
        var credential = Register();

        credential.RevokeForLapsedEntitlement();

        Assert.False(credential.Active);
    }

    [Fact]
    public void RevokeForLapsedEntitlement_ClearsTheTokenCiphertext()
    {
        var credential = Register();

        credential.RevokeForLapsedEntitlement();

        Assert.Empty(credential.TokenCiphertext);
    }

    /// <summary>Unlike <see cref="ChannelCredential.Revoke"/>, which leaves
    /// <see cref="ChannelCredential.TokenCiphertext"/> sitting in the row - the ordinary tenant-
    /// initiated path never had a reason to wipe it, and this test's own sibling
    /// (<see cref="Revoke_FlipsActiveToFalse"/>'s neighbours) never asserts otherwise.</summary>
    [Fact]
    public void Revoke_LeavesTheTokenCiphertextInPlace()
    {
        var credential = Register();

        credential.Revoke();

        Assert.Equal(new byte[] { 1, 2, 3 }, credential.TokenCiphertext);
    }

    [Fact]
    public void RevokeForLapsedEntitlement_WithARefreshToken_ClearsItToo()
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), SiteId, ChannelKind.Avito, [1, 2, 3], Hash("s"), Now,
            providerAccountId: "94235311", refreshTokenCiphertext: [9, 9, 9]);

        credential.RevokeForLapsedEntitlement();

        Assert.NotNull(credential.RefreshTokenCiphertext);
        Assert.Empty(credential.RefreshTokenCiphertext);
    }

    /// <summary>No refresh token to clear for every channel but Avito - clearing must not turn a
    /// <see langword="null"/> (never had one) into an empty array (had one, now cleared), the two
    /// being materially different facts about this credential's own history.</summary>
    [Fact]
    public void RevokeForLapsedEntitlement_WithNoRefreshToken_LeavesItNull()
    {
        var credential = Register();

        credential.RevokeForLapsedEntitlement();

        Assert.Null(credential.RefreshTokenCiphertext);
    }

    [Fact]
    public void RevokeForLapsedEntitlement_WhenAlreadyRevoked_Throws()
    {
        var credential = Register();
        credential.RevokeForLapsedEntitlement();

        Assert.Throws<InvalidChannelCredentialStateException>(() => credential.RevokeForLapsedEntitlement());
    }

    [Fact]
    public void RevokeForLapsedEntitlement_WhenAlreadyRevokedByTheTenant_Throws()
    {
        var credential = Register();
        credential.Revoke();

        Assert.Throws<InvalidChannelCredentialStateException>(() => credential.RevokeForLapsedEntitlement());
    }
}
