using Ago.Chat.Infrastructure.WhatsApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-10`: <see cref="WhatsAppApiClient"/>'s own terminal/transient split, proven against a real HTTP
/// boundary rather than trusting a code comment - <see cref="VkApiClientTests"/>'s own precedent for an
/// in-process, ephemeral-port Kestrel host standing in for the real provider. What this item's own report
/// needs proven: that <see cref="WhatsAppApiClient"/> reads the JSON body's own numeric <c>error.code</c>
/// to decide Refused-vs-thrown even though, unlike VK, the HTTP status itself is also a real non-200 on
/// failure - the fourth, genuinely distinct outcome shape <see cref="WhatsAppApiClient"/>'s own remarks
/// describe.
/// </summary>
public sealed class WhatsAppApiClientTests
{
    private const string Token = "test-system-user-token-not-a-real-secret";
    private const string PhoneNumberId = "106540352242922";

    [Fact]
    public async Task SendMessageAsync_WhenWhatsAppAnswersWithAResponse_ReturnsSentWithTheProviderMessageId()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapPost($"/{PhoneNumberId}/messages", () => Results.Json(
                new { messaging_product = "whatsapp", messages = new[] { new { id = "wamid.abc123" } } })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.SendMessageAsync(Token, PhoneNumberId, "16505551234", "hello", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("wamid.abc123", result.ProviderMessageId);
    }

    /// <summary>131047 ("more than 24 hours have passed since the recipient last replied") is the
    /// customer-service-window refusal this item's own backlog note names by name - confirmed from Meta's
    /// own error-codes documentation as one specific numeric code, terminal because no retry ever turns a
    /// free-form message sent outside the window into one Meta will accept
    /// (<see cref="WhatsAppApiClient"/>'s own remarks).</summary>
    [Fact]
    public async Task SendMessageAsync_WhenWhatsAppRefusesOutsideThe24HourWindow_ReturnsRefusedRatherThanThrowing()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapPost($"/{PhoneNumberId}/messages", () => Results.Json(
                new { error = new { message = "Message failed to send because more than 24 hours have passed since the recipient last replied to this number.", type = "OAuthException", code = 131047, error_subcode = 2018278 } },
                statusCode: 400)));

        var client = BuildClient(host.BaseUrl);

        var result = await client.SendMessageAsync(Token, PhoneNumberId, "16505551234", "hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("131047", result.RefusalReason);
    }

    /// <summary>130429 (Cloud API message throughput limit reached) is deliberately excluded from the
    /// terminal list even though it arrives with the identical non-200-plus-error-body shape as a terminal
    /// refusal - excluding it is what lets the wrapping resilience pipeline's own retry/backoff actually
    /// help, the identical "default-to-transient" reasoning <see cref="VkApiClientTests"/>'s own flood-
    /// control test states for VK's equivalent case.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenWhatsAppReturnsAThroughputLimitErrorCode_ThrowsRatherThanRefusing()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapPost($"/{PhoneNumberId}/messages", () => Results.Json(
                new { error = new { message = "Message throughput limit reached", type = "OAuthException", code = 130429, error_subcode = (int?)null } },
                statusCode: 429)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(Token, PhoneNumberId, "16505551234", "hello", CancellationToken.None));
    }

    /// <summary>The literal "provider unreachable" case, against a real closed socket - the same "actually
    /// stop it" standard <see cref="VkApiClientTests"/>/<see cref="MaxChannelAdapterResilienceTests"/> hold
    /// themselves to.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenWhatsAppIsUnreachable_ThrowsARealConnectionFailure()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapPost($"/{PhoneNumberId}/messages", () => Results.Json(new { messages = new[] { new { id = "wamid.1" } } })));
        var client = BuildClient(host.BaseUrl);
        await host.App.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(Token, PhoneNumberId, "16505551234", "hello", CancellationToken.None));
    }

    [Fact]
    public async Task GetPhoneNumberAsync_WhenWhatsAppAnswersWithANumber_ReturnsIt()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{PhoneNumberId}", () => Results.Json(
                new { id = PhoneNumberId, display_phone_number = "+1 555-555-5555", verified_name = "Test Shop" })));

        var client = BuildClient(host.BaseUrl);

        var info = await client.GetPhoneNumberAsync(Token, PhoneNumberId, CancellationToken.None);

        Assert.Equal(PhoneNumberId, info.Id);
        Assert.Equal("Test Shop", info.VerifiedName);
    }

    [Fact]
    public async Task GetPhoneNumberAsync_WhenWhatsAppRejectsTheToken_ThrowsWhatsAppApiCallException()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{PhoneNumberId}", () => Results.Json(
                new { error = new { message = "Invalid OAuth access token", type = "OAuthException", code = 190, error_subcode = (int?)null } },
                statusCode: 401)));

        var client = BuildClient(host.BaseUrl);

        var ex = await Assert.ThrowsAsync<WhatsAppApiCallException>(
            () => client.GetPhoneNumberAsync(Token, PhoneNumberId, CancellationToken.None));
        Assert.Contains("190", ex.Message);
    }

    private static WhatsAppApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for Meta's own Graph
    /// API - <see cref="VkApiClientTests"/>'s own established technique in this project, reused here for
    /// a fourth channel provider's HTTP boundary.</summary>
    private static async Task<TestHost> BuildFakeWhatsAppHostAsync(Action<WebApplication> configureRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        configureRoutes(app);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }
}
