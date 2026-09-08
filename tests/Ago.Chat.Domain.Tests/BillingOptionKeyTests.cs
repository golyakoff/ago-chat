namespace Ago.Chat.Domain.Tests;

/// <summary>`23-86`: mirrors <see cref="ModuleKeyTests"/> exactly - the identical character rules,
/// proven independently rather than assumed from the sibling type, since <see cref="BillingOptionKey"/>
/// is its own value type with its own constructor (<see cref="BillingOptionKey"/>'s own remarks on
/// why it is not a reuse of <see cref="ModuleKey"/>).</summary>
public class BillingOptionKeyTests
{
    [Theory]
    [InlineData("channel-telegram")]
    [InlineData("a")]
    [InlineData("ai_faq_addon")]
    public void Constructor_AcceptsAValidKey(string value)
    {
        var key = new BillingOptionKey(value);

        Assert.Equal(value, key.Value);
    }

    [Fact]
    public void Constructor_Trims()
    {
        var key = new BillingOptionKey("  channel-telegram  ");

        Assert.Equal("channel-telegram", key.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmpty(string value) =>
        Assert.Throws<ArgumentException>(() => new BillingOptionKey(value));

    [Fact]
    public void Constructor_RejectsTooLong() =>
        Assert.Throws<ArgumentException>(() => new BillingOptionKey(new string('a', BillingOptionKey.MaxLength + 1)));

    [Theory]
    [InlineData("Channel")] // uppercase
    [InlineData("channel.telegram")] // dot
    [InlineData("channel telegram")] // space
    [InlineData("channel/telegram")] // slash
    public void Constructor_RejectsDisallowedCharacters(string value) =>
        Assert.Throws<ArgumentException>(() => new BillingOptionKey(value));

    [Fact]
    public void Equality_IsByValue_LikeAnyOtherRecordStruct()
    {
        Assert.Equal(new BillingOptionKey("channel-telegram"), new BillingOptionKey("channel-telegram"));
    }
}
