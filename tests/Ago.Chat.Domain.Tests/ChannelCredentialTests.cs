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
}
