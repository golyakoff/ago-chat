using System.Net;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;

namespace Ago.Chat.Application.Tests.UseCases.RegisterWebhookEndpoint;

/// <summary>
/// `6-03`'s own Done-when: "SSRF rejection proven... a unit test against the validator, not a live
/// network probe." Every rejection case below is an IP literal or the `localhost` name - none require
/// DNS resolution, matching <see cref="WebhookUrlValidator"/>'s own remarks on why it stays pure.
/// </summary>
public class WebhookUrlValidatorTests
{
    [Theory]
    [InlineData("https://example.com/webhooks/ago")]
    [InlineData("https://api.shop.example.com:8443/hooks")]
    public void Validate_AnOrdinaryHttpsUrl_IsAllowed(string url)
    {
        var result = WebhookUrlValidator.Validate(new Uri(url));

        Assert.Null(result);
    }

    [Fact]
    public void Validate_AnHttpUrl_IsRejected()
    {
        var result = WebhookUrlValidator.Validate(new Uri("http://example.com/webhooks/ago"));

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("https://127.0.0.1/webhooks/ago")]
    [InlineData("https://127.0.0.1:8080/webhooks/ago")]
    [InlineData("https://localhost/webhooks/ago")]
    [InlineData("https://[::1]/webhooks/ago")]
    public void Validate_ALoopbackUrl_IsRejected(string url)
    {
        var result = WebhookUrlValidator.Validate(new Uri(url));

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/")] // cloud metadata endpoint
    [InlineData("https://169.254.0.1/")]
    public void Validate_ALinkLocalUrl_IsRejected(string url)
    {
        var result = WebhookUrlValidator.Validate(new Uri(url));

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("https://10.0.0.5/webhooks/ago")]
    [InlineData("https://172.16.0.1/webhooks/ago")]
    [InlineData("https://172.31.255.255/webhooks/ago")]
    [InlineData("https://192.168.1.1/webhooks/ago")]
    public void Validate_APrivateRangeUrl_IsRejected(string url)
    {
        var result = WebhookUrlValidator.Validate(new Uri(url));

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("https://172.15.255.255/")] // just below 172.16.0.0/12
    [InlineData("https://172.32.0.0/")] // just above 172.16.0.0/12
    [InlineData("https://11.0.0.1/")] // outside 10.0.0.0/8
    public void Validate_AnAddressJustOutsideAPrivateRange_IsAllowed(string url)
    {
        var result = WebhookUrlValidator.Validate(new Uri(url));

        Assert.Null(result);
    }

    [Fact]
    public void Validate_AUniqueLocalIPv6Url_IsRejected()
    {
        var result = WebhookUrlValidator.Validate(new Uri("https://[fd00::1]/webhooks/ago"));

        Assert.NotNull(result);
    }

    /// <summary>
    /// `6-05`: <see cref="WebhookUrlValidator.IsDisallowedResolvedAddress"/> is the same private-range
    /// predicate <see cref="WebhookUrlValidator.Validate"/> already uses for an IP-literal URL, exposed
    /// so `Ago.Chat.Webhooks`'s own delivery-time SSRF recheck (a `SocketsHttpHandler.ConnectCallback`,
    /// `Program.cs`) can apply it to whatever address DNS actually resolves to, not just what a
    /// registration-time URL literally spelled out. These cases mirror the theories above one level
    /// down the stack - a plain <see cref="IPAddress"/>, not a <see cref="Uri"/> to parse first.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public void IsDisallowedResolvedAddress_APrivateLoopbackOrLinkLocalAddress_ReturnsTrue(string address)
    {
        Assert.True(WebhookUrlValidator.IsDisallowedResolvedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("93.184.216.34")] // example.com's own long-standing public address
    [InlineData("8.8.8.8")]
    public void IsDisallowedResolvedAddress_AnOrdinaryPublicAddress_ReturnsFalse(string address)
    {
        Assert.False(WebhookUrlValidator.IsDisallowedResolvedAddress(IPAddress.Parse(address)));
    }
}
