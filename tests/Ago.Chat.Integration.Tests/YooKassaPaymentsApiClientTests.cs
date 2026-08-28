using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.YooKassa;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-02`: <see cref="YooKassaPaymentsApiClient"/>'s own real HTTP boundary, proven against a fake
/// ЮKassa standing in for the real Payments API - <see cref="TelegramApiClientTests"/>'s own precedent
/// (a real, in-process, ephemeral-port Kestrel host, not a mocked <see cref="HttpClient"/>), reused here
/// for a payment provider instead of a channel provider.
///
/// <para><b>What this proves, and what it does not.</b> This proves the exact request shape this codebase
/// sends (amount formatting, `Idempotence-Key` header, `confirmation.return_url`) and that every one of
/// ЮKassa's documented response shapes this class handles is actually handled the way the code claims -
/// it does <b>not</b> prove any of this against ЮKassa's own real service, since this environment has no
/// live Shop ID/Secret Key to call it with (this item's own report explains why that Done-when box stays
/// unticked).</para>
/// </summary>
public sealed class YooKassaPaymentsApiClientTests
{
    [Fact]
    public async Task CreatePaymentAsync_SendsTheDocumentedRequestShape()
    {
        string? capturedIdempotenceKey = null;
        string? capturedAuthorization = null;
        string? capturedBody = null;

        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", async (HttpContext context) =>
            {
                // Captured as plain strings, not the HttpRequest/HttpContext itself - both are
                // disposed once this handler returns, before the assertions below ever run.
                capturedIdempotenceKey = context.Request.Headers["Idempotence-Key"].ToString();
                capturedAuthorization = context.Request.Headers.Authorization.ToString();
                using var reader = new StreamReader(context.Request.Body);
                capturedBody = await reader.ReadToEndAsync();
                return Results.Json(
                    new { id = "pmt_abc", status = "pending", confirmation = new { type = "redirect", confirmation_url = "https://yookassa.example/confirm/abc" } },
                    statusCode: StatusCodes.Status200OK);
            }));

        var client = BuildClient(host.BaseUrl, "shop_1", "secret_1");

        var result = await client.CreatePaymentAsync(
            new CreatePaymentRequest(2500m, "AGO Chat - starter tier, 5 seats", "https://console.example/billing/return", "idem-key-1"),
            CancellationToken.None);

        Assert.IsType<CreatePaymentResult.Success>(result);
        Assert.Equal("idem-key-1", capturedIdempotenceKey);
        Assert.StartsWith("Basic ", capturedAuthorization);
        Assert.Contains("\"value\":\"2500.00\"", capturedBody);
        Assert.Contains("\"currency\":\"RUB\"", capturedBody);
        Assert.Contains("\"type\":\"redirect\"", capturedBody);
        Assert.Contains("\"return_url\":\"https://console.example/billing/return\"", capturedBody);
        Assert.Contains("\"save_payment_method\":true", capturedBody);
    }

    [Fact]
    public async Task CreatePaymentAsync_WhenYooKassaAnswersOk_ReturnsTheConfirmationUrl()
    {
        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.Json(new
            {
                id = "pmt_xyz",
                status = "pending",
                confirmation = new { type = "redirect", confirmation_url = "https://yookassa.example/confirm/xyz" },
            })));

        var client = BuildClient(host.BaseUrl, "shop_1", "secret_1");

        var result = await client.CreatePaymentAsync(
            new CreatePaymentRequest(500m, "test", "https://console.example/return", "idem-key-2"), CancellationToken.None);

        var success = Assert.IsType<CreatePaymentResult.Success>(result);
        Assert.Equal("pmt_xyz", success.PaymentId);
        Assert.Equal("https://yookassa.example/confirm/xyz", success.ConfirmationUrl);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    public async Task CreatePaymentAsync_WhenYooKassaRefusesWithAClientShapedStatus_ReturnsRefusedRatherThanThrowing(int statusCode)
    {
        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.Json(
                new { code = "invalid_request", description = "amount.value is not a valid number" },
                statusCode: statusCode)));

        var client = BuildClient(host.BaseUrl, "shop_1", "secret_1");

        var result = await client.CreatePaymentAsync(
            new CreatePaymentRequest(500m, "test", "https://console.example/return", "idem-key-3"), CancellationToken.None);

        var refused = Assert.IsType<CreatePaymentResult.Refused>(result);
        Assert.Contains(statusCode.ToString(), refused.Reason);
        Assert.Contains("amount.value is not a valid number", refused.Reason);
    }

    [Fact]
    public async Task CreatePaymentAsync_WhenYooKassaReturns429_ThrowsRatherThanRefusing()
    {
        // ЮKassa's own rate limiting is transient and retry-worthy, the same reasoning
        // TelegramApiClient excludes 429 from its own terminal-refusal list for.
        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.StatusCode(StatusCodes.Status429TooManyRequests)));

        var client = BuildClient(host.BaseUrl, "shop_1", "secret_1");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreatePaymentAsync(
            new CreatePaymentRequest(500m, "test", "https://console.example/return", "idem-key-4"), CancellationToken.None));
    }

    [Fact]
    public async Task CreatePaymentAsync_WhenYooKassaReturns500_Throws()
    {
        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.StatusCode(StatusCodes.Status500InternalServerError)));

        var client = BuildClient(host.BaseUrl, "shop_1", "secret_1");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreatePaymentAsync(
            new CreatePaymentRequest(500m, "test", "https://console.example/return", "idem-key-5"), CancellationToken.None));
    }

    [Fact]
    public async Task CreatePaymentAsync_WhenYooKassaIsUnreachable_ThrowsARealConnectionFailure()
    {
        await using var host = await BuildFakeYooKassaHostAsync(app =>
            app.MapPost("payments", () => Results.Json(new { id = "x", status = "pending", confirmation = new { type = "redirect", confirmation_url = "https://x" } })));
        var client = BuildClient(host.BaseUrl, "shop_1", "secret_1");
        await host.App.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreatePaymentAsync(
            new CreatePaymentRequest(500m, "test", "https://console.example/return", "idem-key-6"), CancellationToken.None));
    }

    private static YooKassaPaymentsApiClient BuildClient(string baseUrl, string shopId, string secretKey)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{shopId}:{secretKey}"));
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        return new YooKassaPaymentsApiClient(httpClient);
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for ЮKassa's own
    /// Payments API - <see cref="TelegramApiClientTests"/>'s own established technique in this
    /// project.</summary>
    private static async Task<TestHost> BuildFakeYooKassaHostAsync(Action<WebApplication> configureRoutes)
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
