using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests;

public class ChannelLinkUrlBuilderTests
{
    [Fact]
    public void BuildUrl_ForTelegram_BuildsTheDeepLink()
    {
        var url = ChannelLinkUrlBuilder.BuildUrl(ChannelKind.Telegram, "example_shop_bot");

        Assert.Equal("https://t.me/example_shop_bot", url);
    }

    [Fact]
    public void BuildUrl_ForMax_BuildsTheDeepLinkWithAnAtSign()
    {
        var url = ChannelLinkUrlBuilder.BuildUrl(ChannelKind.Max, "example_shop_bot");

        Assert.Equal("https://max.ru/@example_shop_bot", url);
    }

    [Fact]
    public void BuildUrl_ForVk_BuildsTheCommunityDeepLink()
    {
        var url = ChannelLinkUrlBuilder.BuildUrl(ChannelKind.Vk, "123456");

        Assert.Equal("https://vk.me/club123456", url);
    }

    /// <summary>`25-147`'s own capture is a human-formatted number - the exact display value Meta's own
    /// API returns - and `wa.me` accepts only digits, so this is the one channel whose handle needs real
    /// normalisation at URL-build time rather than a direct substitution.</summary>
    [Fact]
    public void BuildUrl_ForWhatsApp_StripsEverythingButDigits()
    {
        var url = ChannelLinkUrlBuilder.BuildUrl(ChannelKind.WhatsApp, "+1 555 0100");

        Assert.Equal("https://wa.me/15550100", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildUrl_WithAnEmptyOrWhitespaceHandle_ReturnsNull(string handle)
    {
        Assert.Null(ChannelLinkUrlBuilder.BuildUrl(ChannelKind.Telegram, handle));
    }

    /// <summary>Avito never reaches this method with a real handle at all (`IPublicChannelLinkReadStore`'s
    /// own contract never produces a row for it), but this proves the fallback is `null`, not a
    /// fabricated URL, if it ever somehow did.</summary>
    [Fact]
    public void BuildUrl_ForAChannelKindWithNoKnownPublicWebSurface_ReturnsNull()
    {
        Assert.Null(ChannelLinkUrlBuilder.BuildUrl(ChannelKind.Avito, "94235311"));
    }
}
