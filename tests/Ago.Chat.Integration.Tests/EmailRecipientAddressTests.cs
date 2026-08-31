using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Email;

namespace Ago.Chat.Integration.Tests;

/// <summary>`14-09`: <see cref="EmailRecipientAddress"/> is a pure function with no infrastructure
/// dependency of its own - <see cref="WhatsAppInboundMessageParserTests"/>'s own precedent for living in
/// <c>Ago.Chat.Integration.Tests</c> rather than a dedicated <c>Ago.Chat.Infrastructure.Email.Tests</c>
/// project, for the identical pragmatic reason that class states for itself.</summary>
public sealed class EmailRecipientAddressTests
{
    private static EmailBotApiOptions Options(string supportLocalPart = "support") => new()
    {
        Domain = "ago-chat.example",
        SupportLocalPart = supportLocalPart,
    };

    [Fact]
    public void Build_ProducesTheExpectedSubaddressedAddress()
    {
        var siteId = new SiteId(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"));

        var address = EmailRecipientAddress.Build(Options(), siteId);

        Assert.Equal("support+3fa85f6457174562b3fc2c963f66afa6@ago-chat.example", address);
    }

    [Fact]
    public void TryParseSiteId_RoundTripsWhatBuildProduced()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var address = EmailRecipientAddress.Build(Options(), siteId);

        var parsed = EmailRecipientAddress.TryParseSiteId(Options(), address);

        Assert.Equal(siteId, parsed);
    }

    [Fact]
    public void TryParseSiteId_WithADisplayNameWrapper_StillExtractsTheAddress()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var bareAddress = EmailRecipientAddress.Build(Options(), siteId);

        var parsed = EmailRecipientAddress.TryParseSiteId(Options(), $"\"A Visitor\" <{bareAddress}>");

        Assert.Equal(siteId, parsed);
    }

    [Fact]
    public void TryParseSiteId_ForADifferentDomain_ReturnsNull()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var address = $"support+{siteId.Value:N}@a-different-domain.example";

        Assert.Null(EmailRecipientAddress.TryParseSiteId(Options(), address));
    }

    [Fact]
    public void TryParseSiteId_ForADifferentLocalPart_ReturnsNull()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var address = $"sales+{siteId.Value:N}@ago-chat.example";

        Assert.Null(EmailRecipientAddress.TryParseSiteId(Options(), address));
    }

    /// <summary>`10-05`'s own RFC 2142 aliases (<c>postmaster@</c>, <c>abuse@</c>) must never resolve to a
    /// site - they carry no <c>+</c> subaddress at all.</summary>
    [Fact]
    public void TryParseSiteId_ForAPlainRfc2142Alias_ReturnsNull()
    {
        Assert.Null(EmailRecipientAddress.TryParseSiteId(Options(), "postmaster@ago-chat.example"));
    }

    [Fact]
    public void TryParseSiteId_WithANonGuidSuffix_ReturnsNull()
    {
        Assert.Null(EmailRecipientAddress.TryParseSiteId(Options(), "support+not-a-guid@ago-chat.example"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseSiteId_WithNoAddress_ReturnsNull(string? address)
    {
        Assert.Null(EmailRecipientAddress.TryParseSiteId(Options(), address));
    }

    [Fact]
    public void TryParseSiteId_WhenDomainIsNotConfigured_ReturnsNull()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var address = $"support+{siteId.Value:N}@ago-chat.example";

        Assert.Null(EmailRecipientAddress.TryParseSiteId(new EmailBotApiOptions { Domain = "" }, address));
    }

    [Fact]
    public void TryParseSiteId_IsCaseInsensitiveOnTheLocalPartAndDomain()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var address = $"SUPPORT+{siteId.Value:N}@AGO-CHAT.EXAMPLE";

        Assert.Equal(siteId, EmailRecipientAddress.TryParseSiteId(Options(), address));
    }
}
