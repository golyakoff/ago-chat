namespace Ago.Chat.Domain.Tests;

/// <summary>`23-85`/`adr/0151`: proves the one-entitlement-per-channel-kind mapping is total (every
/// <see cref="ChannelKind"/> member has a billing option key - a channel added without a matching
/// `switch` arm in <see cref="ChannelEntitlementOptionKeys.For"/> would throw at runtime, which this
/// test turns into a compile-and-test-time signal instead) and that no two kinds collide on the same
/// key.</summary>
public class ChannelEntitlementOptionKeysTests
{
    [Theory]
    [InlineData(ChannelKind.Max, "channel-max")]
    [InlineData(ChannelKind.Sms, "channel-sms")]
    [InlineData(ChannelKind.Telegram, "channel-telegram")]
    [InlineData(ChannelKind.WhatsApp, "channel-whatsapp")]
    [InlineData(ChannelKind.Vk, "channel-vk")]
    [InlineData(ChannelKind.Avito, "channel-avito")]
    [InlineData(ChannelKind.Email, "channel-email")]
    public void For_ReturnsTheExpectedOptionKey(ChannelKind kind, string expected)
    {
        var optionKey = ChannelEntitlementOptionKeys.For(kind);

        Assert.Equal(expected, optionKey.Value);
    }

    /// <summary>Every <see cref="ChannelKind"/> member this assembly actually defines resolves without
    /// throwing - the guard against a new channel kind being added with no matching price-list key
    /// having been considered (<see cref="ChannelEntitlementOptionKeys.For"/>'s own remarks on why the
    /// mapping is a plain <see langword="switch"/> rather than a computed string).</summary>
    [Fact]
    public void For_IsDefinedForEveryChannelKind()
    {
        foreach (var kind in Enum.GetValues<ChannelKind>())
        {
            var optionKey = ChannelEntitlementOptionKeys.For(kind);
            Assert.False(string.IsNullOrWhiteSpace(optionKey.Value));
        }
    }

    [Fact]
    public void For_NeverReturnsTheSameKeyForTwoDifferentChannelKinds()
    {
        var keys = Enum.GetValues<ChannelKind>().Select(ChannelEntitlementOptionKeys.For).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
