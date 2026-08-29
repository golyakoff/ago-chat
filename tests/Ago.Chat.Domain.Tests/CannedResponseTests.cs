namespace Ago.Chat.Domain.Tests;

/// <summary>`18-03`: the prepared-answer value object - a pure constructor with no clock, no database
/// and nothing to fake (testing.md's domain-unit level), the same shape `OfflineAutoReplyRule`'s own
/// tests use.</summary>
public class CannedResponseTests
{
    [Theory]
    [InlineData("", "Refunds take three working days.")]
    [InlineData("   ", "Refunds take three working days.")]
    public void Constructor_WithAnEmptyTitle_Throws(string title, string body) =>
        Assert.Throws<ArgumentException>(() => new CannedResponse(title, body));

    [Theory]
    [InlineData("Refund policy", "")]
    [InlineData("Refund policy", "   ")]
    public void Constructor_WithAnEmptyBody_Throws(string title, string body) =>
        Assert.Throws<ArgumentException>(() => new CannedResponse(title, body));

    [Fact]
    public void Constructor_WithAnOversizedTitle_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new CannedResponse(new string('t', CannedResponse.MaxTitleLength + 1), "Reply text."));

    [Fact]
    public void Constructor_WithAnOversizedBody_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new CannedResponse("Refund policy", new string('b', CannedResponse.MaxBodyLength + 1)));

    [Fact]
    public void Constructor_WhenValid_SetsProperties()
    {
        var response = new CannedResponse("Refund policy", "Refunds take three working days.");

        Assert.Equal("Refund policy", response.Title);
        Assert.Equal("Refunds take three working days.", response.Body);
    }

    [Fact]
    public void Constructor_TrimsTitle()
    {
        var response = new CannedResponse("  Refund policy  ", "Refunds take three working days.");

        Assert.Equal("Refund policy", response.Title);
    }

    [Fact]
    public void Constructor_DoesNotTrimBody()
    {
        // Unlike Title (a label), Body is inserted verbatim into the composer - trimming it would be
        // a silent content change to text an operator wrote on purpose (e.g. deliberate leading
        // whitespace inside a formatted reply).
        var response = new CannedResponse("Greeting", "  Hello there.  ");

        Assert.Equal("  Hello there.  ", response.Body);
    }

    [Fact]
    public void MaxBodyLength_MatchesMessageBodysOwnLimit() =>
        // The load-bearing claim in this type's own doc comment - a canned response is inserted into
        // the composer and can be sent as-is, so it must fit whatever a real message can hold, no
        // smaller ceiling invented on top.
        Assert.Equal(MessageBody.MaxLength, CannedResponse.MaxBodyLength);
}
