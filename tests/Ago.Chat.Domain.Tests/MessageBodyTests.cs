namespace Ago.Chat.Domain.Tests;

public class MessageBodyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WhenValueIsEmptyOrWhitespace_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => new MessageBody(value));
    }

    [Fact]
    public void Constructor_WhenValueExceedsMaxLength_Throws()
    {
        var tooLong = new string('a', MessageBody.MaxLength + 1);

        Assert.Throws<ArgumentException>(() => new MessageBody(tooLong));
    }

    [Fact]
    public void Constructor_WhenValueIsValid_SetsValue()
    {
        var body = new MessageBody("hello there");

        Assert.Equal("hello there", body.Value);
    }

    [Fact]
    public void Constructor_WhenValueIsExactlyMaxLength_Succeeds()
    {
        var exact = new string('a', MessageBody.MaxLength);

        var body = new MessageBody(exact);

        Assert.Equal(MessageBody.MaxLength, body.Value.Length);
    }
}
