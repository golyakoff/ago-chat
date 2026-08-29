namespace Ago.Chat.Domain.Tests;

public class ModuleKeyTests
{
    [Theory]
    [InlineData("calendar")]
    [InlineData("a")]
    [InlineData("acme-scheduler_v2")]
    public void Constructor_AcceptsAValidKey(string value)
    {
        var key = new ModuleKey(value);

        Assert.Equal(value, key.Value);
    }

    [Fact]
    public void Constructor_Trims()
    {
        var key = new ModuleKey("  calendar  ");

        Assert.Equal("calendar", key.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmpty(string value) =>
        Assert.Throws<ArgumentException>(() => new ModuleKey(value));

    [Fact]
    public void Constructor_RejectsTooLong() =>
        Assert.Throws<ArgumentException>(() => new ModuleKey(new string('a', ModuleKey.MaxLength + 1)));

    [Theory]
    [InlineData("Calendar")] // uppercase
    [InlineData("calendar.booking")] // dot - the one character MessageContentKind allows that this does not
    [InlineData("calendar booking")] // space
    [InlineData("calendar/booking")] // slash
    public void Constructor_RejectsDisallowedCharacters(string value) =>
        Assert.Throws<ArgumentException>(() => new ModuleKey(value));
}
