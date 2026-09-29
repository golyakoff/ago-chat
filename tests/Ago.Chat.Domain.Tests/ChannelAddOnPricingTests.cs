namespace Ago.Chat.Domain.Tests;

/// <summary>`26-278`: <see cref="ChannelAddOnPricing.PriceKeyFor"/> - every `channel-*` option key
/// resolves to the one flat <see cref="ChannelAddOnPricing.ChannelAddOnKey"/>, and every option key this
/// codebase does not yet know how to price (in particular, an AI option) resolves to
/// <see langword="null"/> - the signal <see cref="Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewalHandler"/>
/// still throws on, unchanged from before this item.</summary>
public class ChannelAddOnPricingTests
{
    [Theory]
    [InlineData("channel-max")]
    [InlineData("channel-sms")]
    [InlineData("channel-telegram")]
    [InlineData("channel-whatsapp")]
    [InlineData("channel-vk")]
    [InlineData("channel-avito")]
    [InlineData("channel-email")]
    public void PriceKeyFor_EveryChannelOptionKey_ResolvesToTheFlatChannelAddOnKey(string optionKeyValue)
    {
        var priceKey = ChannelAddOnPricing.PriceKeyFor(new BillingOptionKey(optionKeyValue));

        Assert.Equal(ChannelAddOnPricing.ChannelAddOnKey, priceKey);
    }

    /// <summary>Every real <see cref="ChannelKind"/> this assembly defines, resolved through
    /// <see cref="ChannelEntitlementOptionKeys.For"/> first exactly as a real caller would - not just the
    /// literal strings above - proving the two types agree with each other, not only with a hand-typed
    /// list.</summary>
    [Fact]
    public void PriceKeyFor_EveryRealChannelKindsOwnOptionKey_ResolvesToTheFlatChannelAddOnKey()
    {
        foreach (var kind in Enum.GetValues<ChannelKind>())
        {
            var optionKey = ChannelEntitlementOptionKeys.For(kind);

            Assert.Equal(ChannelAddOnPricing.ChannelAddOnKey, ChannelAddOnPricing.PriceKeyFor(optionKey));
        }
    }

    /// <summary>`0012`'s own "для ИИ - нет, и поэтому цены не публикуются" - an AI option is
    /// deliberately unpriced, and this is the exact `null` the renewal handler still throws on.</summary>
    [Fact]
    public void PriceKeyFor_AnAiOptionKey_ReturnsNull()
    {
        var priceKey = ChannelAddOnPricing.PriceKeyFor(new BillingOptionKey("ai-processing"));

        Assert.Null(priceKey);
    }
}
