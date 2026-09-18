using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-148`: <see cref="PublicChannelLinkReadStore"/> against real Postgres - the SQL itself is the thing
/// worth proving directly, before it ever reaches the wire through <c>AuthEndpoints</c>: the `coalesce`
/// that derives VK's own handle at read time (`25-147`'s own decision), and the exclusion of every row
/// with no known handle at all (Avito, always; MAX/Telegram before their own `getMe` capture succeeds).
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class PublicChannelLinkReadStoreTests(SiteCachingFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetForSiteAsync_ForASiteWithNoConnectedChannels_ReturnsAnEmptyList()
    {
        var siteId = await SeedSiteAsync();
        var store = new PublicChannelLinkReadStore(fixture.DataSource);

        var links = await store.GetForSiteAsync(siteId, CancellationToken.None);

        Assert.Empty(links);
    }

    [Fact]
    public async Task GetForSiteAsync_ForATelegramCredentialWithAPublicHandle_ReturnsIt()
    {
        var siteId = await SeedSiteAsync();
        await SeedCredentialAsync(siteId, ChannelKind.Telegram, publicHandle: "shop_support_bot");
        var store = new PublicChannelLinkReadStore(fixture.DataSource);

        var links = await store.GetForSiteAsync(siteId, CancellationToken.None);

        var link = Assert.Single(links);
        Assert.Equal(ChannelKind.Telegram, link.Kind);
        Assert.Equal("shop_support_bot", link.Handle);
    }

    /// <summary>A MAX/Telegram credential connected but not yet capture'd (`25-147`'s own best-effort
    /// or not-yet-read-status-page case) must produce no row at all - never a row with a null or empty
    /// handle a caller would have to filter out itself.</summary>
    [Fact]
    public async Task GetForSiteAsync_ForATelegramCredentialWithNoPublicHandleYet_ProducesNoRow()
    {
        var siteId = await SeedSiteAsync();
        await SeedCredentialAsync(siteId, ChannelKind.Telegram, publicHandle: null);
        var store = new PublicChannelLinkReadStore(fixture.DataSource);

        var links = await store.GetForSiteAsync(siteId, CancellationToken.None);

        Assert.Empty(links);
    }

    /// <summary>`25-147`'s own read-time-derivation decision, proven directly against the SQL: VK's own
    /// `public_handle` column is never populated, yet this store still returns the community id, derived
    /// from `provider_account_id`.</summary>
    [Fact]
    public async Task GetForSiteAsync_ForAVkCredential_DerivesTheHandleFromProviderAccountId()
    {
        var siteId = await SeedSiteAsync();
        await SeedCredentialAsync(siteId, ChannelKind.Vk, publicHandle: null, providerAccountId: "555555");
        var store = new PublicChannelLinkReadStore(fixture.DataSource);

        var links = await store.GetForSiteAsync(siteId, CancellationToken.None);

        var link = Assert.Single(links);
        Assert.Equal(ChannelKind.Vk, link.Kind);
        Assert.Equal("555555", link.Handle);
    }

    /// <summary>Avito's own `provider_account_id` (a real numeric seller id, `25-147`'s own remarks) must
    /// never be mistaken for a usable handle the way VK's is - the `coalesce`'s `case` only ever matches
    /// `Kind = 'Vk'`.</summary>
    [Fact]
    public async Task GetForSiteAsync_ForAnAvitoCredential_ProducesNoRow_EvenThoughProviderAccountIdIsSet()
    {
        var siteId = await SeedSiteAsync();
        await SeedCredentialAsync(siteId, ChannelKind.Avito, publicHandle: null, providerAccountId: "94235311");
        var store = new PublicChannelLinkReadStore(fixture.DataSource);

        var links = await store.GetForSiteAsync(siteId, CancellationToken.None);

        Assert.Empty(links);
    }

    [Fact]
    public async Task GetForSiteAsync_ForARevokedCredential_ProducesNoRow()
    {
        var siteId = await SeedSiteAsync();
        var credential = await SeedCredentialAsync(siteId, ChannelKind.Telegram, publicHandle: "shop_support_bot");
        await using (var db = fixture.CreateDbContext())
        {
            var tracked = await db.ChannelCredentials.FindAsync([credential.Id]);
            tracked!.Revoke();
            await db.SaveChangesAsync();
        }

        var store = new PublicChannelLinkReadStore(fixture.DataSource);
        var links = await store.GetForSiteAsync(siteId, CancellationToken.None);

        Assert.Empty(links);
    }

    [Fact]
    public async Task GetForSiteAsync_ReturnsOneRowPerConnectedHandleBearingChannel()
    {
        var siteId = await SeedSiteAsync();
        await SeedCredentialAsync(siteId, ChannelKind.Telegram, publicHandle: "shop_support_bot");
        await SeedCredentialAsync(siteId, ChannelKind.WhatsApp, publicHandle: "+1 555 0100");
        var store = new PublicChannelLinkReadStore(fixture.DataSource);

        var links = await store.GetForSiteAsync(siteId, CancellationToken.None);

        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.Kind == ChannelKind.Telegram && l.Handle == "shop_support_bot");
        Assert.Contains(links, l => l.Kind == ChannelKind.WhatsApp && l.Handle == "+1 555 0100");
    }

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<ChannelCredential> SeedCredentialAsync(
        SiteId siteId, ChannelKind kind, string? publicHandle, string? providerAccountId = null)
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, kind, tokenCiphertext: [1, 2, 3],
            webhookSecretHash: [4, 5, 6], Now, providerAccountId: providerAccountId, publicHandle: publicHandle);

        await using var db = fixture.CreateDbContext();
        db.ChannelCredentials.Add(credential);
        await db.SaveChangesAsync();

        return credential;
    }
}
