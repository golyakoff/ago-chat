using System.Text.Json;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteByPublicKey;

/// <summary>
/// `14-04`: <see cref="SiteConfigDto"/> is a <em>cached</em> shape, which means it is written to Redis
/// as JSON by one process and read back by another (<c>Ago.Platform.Caching.Redis.RedisCache</c>,
/// plain <c>JsonSerializer</c> with default options). Nothing enforced that until now, and adding a
/// field with a constructor that validates is exactly the change that can break it: a value object
/// that refuses a shape <c>System.Text.Json</c> happens to produce on the way back turns a cache
/// <em>hit</em> - not a miss, not a cold start - into a throw, on a path where a miss would have
/// worked fine. That failure mode is invisible to every test that only ever reads the DTO it just
/// constructed.
///
/// <para>Found for real while building `14-04`: the auto-reply consumer's first read of a site
/// populated the entry and worked, and its second read - the cache hit - was where the shape had to
/// survive a round trip.</para>
/// </summary>
public class SiteConfigCacheRoundTripTests
{
    private static SiteConfigDto Dto(
        OfflineAutoReplySettings autoReply,
        Locale locale = Locale.En,
        string? noticeText = "We read what you send us.",
        string? noticeUrl = "https://tenant.example/privacy") =>
        new(
            Guid.NewGuid(), "shop_7f3a", ["https://example.com"], "#336699", Position.BottomLeft, locale, autoReply,
            "free", noticeText, noticeUrl);

    private static SiteConfigDto RoundTrip(SiteConfigDto dto) =>
        JsonSerializer.Deserialize<SiteConfigDto>(JsonSerializer.Serialize(dto))!;

    [Fact]
    public void ADisabledAutoReply_SurvivesTheCache()
    {
        var read = RoundTrip(Dto(OfflineAutoReplySettings.Disabled));

        Assert.False(read.OfflineAutoReply.Enabled);
        Assert.Empty(read.OfflineAutoReply.Rules);
    }

    [Fact]
    public void AConfiguredAutoReply_SurvivesTheCache_RulesAndOrderIntact()
    {
        var read = RoundTrip(Dto(new OfflineAutoReplySettings(
            enabled: true,
            fallbackReply: "We are closed.",
            rules:
            [
                new OfflineAutoReplyRule("delivery", "Two working days."),
                new OfflineAutoReplyRule("refund", "Three working days."),
            ])));

        Assert.True(read.OfflineAutoReply.Enabled);
        Assert.Equal("We are closed.", read.OfflineAutoReply.FallbackReply);
        Assert.Equal(2, read.OfflineAutoReply.Rules.Count);
        Assert.Equal("delivery", read.OfflineAutoReply.Rules[0].Keyword);
        Assert.Equal("Three working days.", read.OfflineAutoReply.Rules[1].Reply);
        // The matcher has to still work on the copy that came back, not just on the original.
        Assert.Equal("Three working days.", read.OfflineAutoReply.Match("about my refund"));
    }

    [Fact]
    public void TheRestOfTheCachedShape_SurvivesToo()
    {
        var dto = Dto(OfflineAutoReplySettings.Disabled);

        var read = RoundTrip(dto);

        Assert.Equal(dto.SiteId, read.SiteId);
        Assert.Equal(dto.PublicKey, read.PublicKey);
        Assert.Equal(dto.AllowedOrigins, read.AllowedOrigins);
        Assert.Equal(dto.WidgetPrimaryColorHex, read.WidgetPrimaryColorHex);
        Assert.Equal(dto.WidgetPosition, read.WidgetPosition);
        Assert.Equal(dto.WidgetLocale, read.WidgetLocale);
        Assert.Equal(dto.WidgetNoticeText, read.WidgetNoticeText);
        Assert.Equal(dto.WidgetNoticeUrl, read.WidgetNoticeUrl);
    }

    // `16-04`: both fields are plain nullable strings with no validating constructor of their own at
    // this DTO layer - so, like `Locale` above, they cannot reproduce `14-04`'s own struct/class bug
    // directly - but asserted through a real round trip anyway, for the same reason: only a round trip
    // through the actual (de)serializer is evidence, not the fact that the test compiles.
    [Fact]
    public void TheWidgetNoticeFields_SurviveTheCache()
    {
        var read = RoundTrip(Dto(OfflineAutoReplySettings.Disabled));

        Assert.Equal("We read what you send us.", read.WidgetNoticeText);
        Assert.Equal("https://tenant.example/privacy", read.WidgetNoticeUrl);
    }

    // `16-04`'s own default - a site with no notice configured must round-trip as `null`, not as an
    // empty string a naive (de)serializer default could substitute.
    [Fact]
    public void ANullWidgetNotice_SurvivesTheCacheAsNull()
    {
        var read = RoundTrip(Dto(OfflineAutoReplySettings.Disabled, noticeText: null, noticeUrl: null));

        Assert.Null(read.WidgetNoticeText);
        Assert.Null(read.WidgetNoticeUrl);
    }

    // `11-10`: `Locale` is a plain CLR enum, not a value object with a validating constructor - so it
    // cannot reproduce 14-04's own struct/class bug directly (there is no constructor for
    // System.Text.Json to bypass). Asserted on a cache *hit* anyway, both non-default values, because
    // this file's whole point is that "it compiles and the test I wrote passes" is not evidence for a
    // cached shape - only a round trip through the same (de)serializer the cache actually uses is.
    [Theory]
    [InlineData(Locale.En)]
    [InlineData(Locale.Ru)]
    public void TheWidgetLocale_SurvivesTheCache(Locale locale)
    {
        var read = RoundTrip(Dto(OfflineAutoReplySettings.Disabled, locale));

        Assert.Equal(locale, read.WidgetLocale);
    }
}
