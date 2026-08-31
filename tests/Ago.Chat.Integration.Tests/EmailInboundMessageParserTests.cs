using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Email;

namespace Ago.Chat.Integration.Tests;

/// <summary>`14-09`: <see cref="EmailInboundMessageParser"/> - the parser's own logic (required-field
/// checks, site resolution from the recipient address, default subject) against this item's own invented
/// wire contract (<see cref="EmailInboundWebhookPayload"/>'s own honesty note explains there is no real
/// third-party shape to confirm this against).</summary>
public sealed class EmailInboundMessageParserTests
{
    private static readonly SiteId SiteId = new(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"));

    private static EmailBotApiOptions Options() => new() { Domain = "ago-chat.example", SupportLocalPart = "support" };

    private static EmailInboundWebhookPayload ValidPayload(
        string? from = "visitor@example.com",
        string? to = "support+3fa85f6457174562b3fc2c963f66afa6@ago-chat.example",
        string? subject = "Where is my order?",
        string? text = "Hi, I have not received my order yet.",
        string? messageId = "<abc123@example.com>") =>
        new(from, to, subject, text, messageId);

    [Fact]
    public void Parse_ForAWellFormedMessage_ExtractsEverything()
    {
        var parsed = EmailInboundMessageParser.Parse(ValidPayload(), Options());

        Assert.NotNull(parsed);
        Assert.Equal(SiteId, parsed!.SiteId);
        Assert.Equal("visitor@example.com", parsed.From);
        Assert.Equal("<abc123@example.com>", parsed.ExternalMessageId);
        Assert.Equal("Where is my order?", parsed.Subject);
        Assert.Equal("Hi, I have not received my order yet.", parsed.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_WithNoFrom_ReturnsNull(string? from)
    {
        Assert.Null(EmailInboundMessageParser.Parse(ValidPayload(from: from), Options()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_WithNoMessageId_ReturnsNull(string? messageId)
    {
        Assert.Null(EmailInboundMessageParser.Parse(ValidPayload(messageId: messageId), Options()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithNoText_ReturnsNull(string? text)
    {
        Assert.Null(EmailInboundMessageParser.Parse(ValidPayload(text: text), Options()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_WithNoSubject_UsesTheDefaultSubject(string? subject)
    {
        var parsed = EmailInboundMessageParser.Parse(ValidPayload(subject: subject), Options());

        Assert.NotNull(parsed);
        Assert.Equal(EmailInboundMessageParser.DefaultSubject, parsed!.Subject);
    }

    /// <summary>The central routing claim - EmailRecipientAddress's own remarks: a recipient that does
    /// not match this deployment's own subaddress shape resolves to no site at all, and the message is
    /// dropped rather than attributed to a guess.</summary>
    [Fact]
    public void Parse_WhenTheRecipientDoesNotResolveToASite_ReturnsNull()
    {
        var parsed = EmailInboundMessageParser.Parse(ValidPayload(to: "someone-else@a-different-domain.example"), Options());

        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_TrimsFromAndMessageId()
    {
        var parsed = EmailInboundMessageParser.Parse(
            ValidPayload(from: "  visitor@example.com  ", messageId: "  <abc123@example.com>  "), Options());

        Assert.NotNull(parsed);
        Assert.Equal("visitor@example.com", parsed!.From);
        Assert.Equal("<abc123@example.com>", parsed.ExternalMessageId);
    }
}
