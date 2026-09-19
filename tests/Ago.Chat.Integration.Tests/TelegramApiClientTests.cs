using Ago.Chat.Infrastructure.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-07`: <see cref="TelegramApiClient"/>'s own terminal/transient split - <see cref="MaxChannelAdapterResilienceTests"/>'
/// own precedent for proving a channel provider's real HTTP boundary rather than trusting a code
/// comment, scaled down deliberately: MAX's own version stands up a whole separate process
/// (<c>Ago.Chat.FakeMax</c>) so it can prove a real *process* being killed degrades gracefully through
/// the full resilience pipeline - that mechanism (breaker/timeout/bulkhead) is already proven generically
/// by <c>ResilientInboundChannelAdapterTests</c> (`14-01`) and concretely against a real provider by
/// <see cref="MaxChannelAdapterResilienceTests"/> itself, and this item found no reason a third proof of
/// the identical Polly wiring would earn its own separate-process fixture. What this item's own
/// Done-when actually needs proven - Telegram's real error shape, and which of its status codes this
/// client treats as terminal versus transient - needs only a real HTTP boundary that can answer
/// different status codes and be shut down mid-test, which a real (in-process, ephemeral-port) Kestrel
/// host gives for a fraction of the ceremony, the same host-per-test technique
/// <see cref="ForwardedHeadersTests"/> already established in this project.
/// </summary>
public sealed class TelegramApiClientTests
{
    private const string Token = "123456:test-token-not-a-real-secret";

    [Fact]
    public async Task SendMessageAsync_WhenTelegramAnswersOk_ReturnsSentWithTheProviderMessageId()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapPost($"/bot{Token}/sendMessage", () =>
                Results.Json(new { ok = true, result = new { message_id = 555 } })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.SendMessageAsync(Token, chatId: 42, text: "hello", requestContact: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("555", result.ProviderMessageId);
    }

    /// <summary>`25-152`: <c>requestContact: false</c> (every ordinary reply) must not add a
    /// <c>reply_markup</c> field at all - not an empty one, not a keyboard with zero rows - proving the
    /// "byte-identical to today" claim <c>OutboundChannelMessage.RequestContactIfSupported</c>'s own
    /// remarks make for every channel that does not set the flag is honoured even by Telegram itself on
    /// its own unset path, the same request shape this client has always sent.</summary>
    [Fact]
    public async Task SendMessageAsync_WithRequestContactFalse_SendsNoReplyMarkupField()
    {
        string? capturedBody = null;
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapPost($"/bot{Token}/sendMessage", async (HttpContext ctx) =>
            {
                capturedBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
                return Results.Json(new { ok = true, result = new { message_id = 1 } });
            }));
        var client = BuildClient(host.BaseUrl);

        await client.SendMessageAsync(Token, chatId: 42, text: "hello", requestContact: false, CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("reply_markup", capturedBody);
    }

    /// <summary>`25-152`'s own outbound half: a phone-collection step's own flag reaches Telegram as a
    /// <c>ReplyKeyboardMarkup</c> carrying exactly one <c>request_contact</c> button, alongside the
    /// unchanged prose text - proven against the real wire body, not just the DTO in isolation.</summary>
    [Fact]
    public async Task SendMessageAsync_WithRequestContactTrue_SendsAReplyKeyboardWithARequestContactButton()
    {
        string? capturedBody = null;
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapPost($"/bot{Token}/sendMessage", async (HttpContext ctx) =>
            {
                capturedBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
                return Results.Json(new { ok = true, result = new { message_id = 1 } });
            }));
        var client = BuildClient(host.BaseUrl);

        await client.SendMessageAsync(
            Token, chatId: 42, text: "What is your phone number?", requestContact: true, CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var document = System.Text.Json.JsonDocument.Parse(capturedBody);
        var keyboard = document.RootElement.GetProperty("reply_markup").GetProperty("keyboard");
        var firstRow = keyboard[0];
        Assert.Single(firstRow.EnumerateArray());
        Assert.True(firstRow[0].GetProperty("request_contact").GetBoolean());
        Assert.Equal("What is your phone number?", document.RootElement.GetProperty("text").GetString());
    }

    /// <summary>403 is Telegram's own well-known shape for "the bot was blocked by this user" - a
    /// permanent condition, never worth retrying, so it must come back as a refusal rather than an
    /// exception. See <see cref="TelegramApiClient"/>'s own remarks for the full reasoning.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenTelegramRefusesWith403_ReturnsRefusedRatherThanThrowing()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapPost($"/bot{Token}/sendMessage", () =>
                Results.Json(
                    new { ok = false, error_code = 403, description = "Forbidden: bot was blocked by the user" },
                    statusCode: StatusCodes.Status403Forbidden)));

        var client = BuildClient(host.BaseUrl);

        var result = await client.SendMessageAsync(Token, chatId: 42, text: "hello", requestContact: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("403", result.RefusalReason);
    }

    /// <summary>The one status code this item's own reasoning deliberately excludes from the terminal
    /// list even though it is 4xx: Telegram's own rate limiting is transient and retry-worthy, unlike a
    /// permanently blocked chat - excluding it is what lets the wrapping resilience pipeline's backoff
    /// actually help, rather than every rate-limited send being silently given up on.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenTelegramReturns429_ThrowsRatherThanRefusing()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapPost($"/bot{Token}/sendMessage", () =>
                Results.Json(
                    new { ok = false, error_code = 429, description = "Too Many Requests" },
                    statusCode: StatusCodes.Status429TooManyRequests)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(Token, chatId: 42, text: "hello", requestContact: false, CancellationToken.None));
    }

    [Fact]
    public async Task SendMessageAsync_WhenTelegramReturns500_Throws()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapPost($"/bot{Token}/sendMessage", () => Results.StatusCode(StatusCodes.Status500InternalServerError)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(Token, chatId: 42, text: "hello", requestContact: false, CancellationToken.None));
    }

    /// <summary>The literal "provider unreachable" case, against a real closed socket rather than a
    /// simulated one - the host is started, its base URL captured, then stopped before the client ever
    /// calls it, so the connection this test observes failing is a real refused TCP connection, the same
    /// "actually stop it" standard <see cref="MaxChannelAdapterResilienceTests"/> holds itself to.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenTelegramIsUnreachable_ThrowsARealConnectionFailure()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapPost($"/bot{Token}/sendMessage", () => Results.Json(new { ok = true, result = new { message_id = 1 } })));
        var client = BuildClient(host.BaseUrl);
        await host.App.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(Token, chatId: 42, text: "hello", requestContact: false, CancellationToken.None));
    }

    [Fact]
    public async Task GetUpdatesAsync_ParsesTheUpdatesEnvelope()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getUpdates", () => Results.Json(new
            {
                ok = true,
                result = new[]
                {
                    new
                    {
                        update_id = 100,
                        message = new
                        {
                            message_id = 7,
                            from = new { id = 9 },
                            chat = new { id = 42 },
                            text = "hi",
                        },
                    },
                },
            })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetUpdatesAsync(Token, offset: null, timeoutSeconds: 1, CancellationToken.None);

        var update = Assert.Single(result.Updates);
        Assert.Equal(100, update.UpdateId);
        Assert.Equal(42, update.Message?.Chat?.Id);
        Assert.Equal("hi", update.Message?.Text);
    }

    [Fact]
    public async Task GetMeAsync_WhenTelegramAnswersOk_ReturnsSuccess()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getMe", () => Results.Json(new { ok = true, result = new { id = 1, is_bot = true } })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.True(result.Ok);
    }

    /// <summary>`25-147`: the field this call was always fetching and throwing away -
    /// <see cref="TelegramApiClient.GetMeAsync"/>'s own remarks on why this is the one new fact this
    /// item's own connect-time capture needs.</summary>
    [Fact]
    public async Task GetMeAsync_WhenTelegramAnswersOk_ReturnsTheBotsOwnUsername()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getMe", () =>
                Results.Json(new { ok = true, result = new { id = 1, is_bot = true, username = "shop_support_bot" } })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.Equal("shop_support_bot", result.Username);
    }

    /// <summary>A bot with no username configured is real (Telegram allows creating one without setting
    /// a public handle) - this must not throw or fabricate a value, only report nothing is known.</summary>
    [Fact]
    public async Task GetMeAsync_WhenTelegramAnswersOkWithNoUsername_ReturnsNullUsername()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getMe", () => Results.Json(new { ok = true, result = new { id = 1, is_bot = true } })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.Null(result.Username);
    }

    /// <summary>401 is Telegram's own well-known shape for an invalid token - the exact case
    /// <see cref="Api.Channels.TelegramChannelEndpoints"/> relies on to reject a bad token immediately at
    /// registration.</summary>
    [Fact]
    public async Task GetMeAsync_WhenTelegramRejectsWith401_ReturnsRefused()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getMe", () =>
                Results.Json(
                    new { ok = false, error_code = 401, description = "Unauthorized" },
                    statusCode: StatusCodes.Status401Unauthorized)));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("401", result.RefusalReason);
    }

    // -----------------------------------------------------------------------------------------
    // `25-164`: DownloadImageAsync's own two-step protocol - getFile resolves a `file_id` to a
    // `file_path`, then a second request against a distinct URL shape (`file/bot<token>/...`, not
    // `bot<token>/{method}`) fetches the actual bytes. Both steps are proven against a real Kestrel
    // host, the same standard every other method in this class already gets.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadImageAsync_WhenGetFileAndDownloadBothSucceed_ReturnsTheBytesAndContentType()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
        {
            app.MapGet($"/bot{Token}/getFile", () =>
                Results.Json(new { ok = true, result = new { file_path = "photos/file_1.jpg" } }));
            app.MapGet($"/file/bot{Token}/photos/file_1.jpg", () =>
                Results.File([1, 2, 3, 4], "image/jpeg"));
        });
        var client = BuildClient(host.BaseUrl);

        var result = await client.DownloadImageAsync(Token, "abc-file-id", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal([1, 2, 3, 4], result.Content);
        Assert.Equal("image/jpeg", result.ContentType);
    }

    /// <summary>Telegram's own documented behaviour for a file over the 20 MB `getFile` ceiling: no
    /// `file_path` at all, not a distinct error - this must degrade to "nothing to download", not
    /// throw.</summary>
    [Fact]
    public async Task DownloadImageAsync_WhenGetFileReturnsNoFilePath_ReturnsNull()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getFile", () => Results.Json(new { ok = true, result = new { } })));
        var client = BuildClient(host.BaseUrl);

        var result = await client.DownloadImageAsync(Token, "abc-file-id", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadImageAsync_WhenGetFileRefusesWith400_ReturnsNullRatherThanThrowing()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getFile", () =>
                Results.Json(
                    new { ok = false, error_code = 400, description = "Bad Request: file not found" },
                    statusCode: StatusCodes.Status400BadRequest)));
        var client = BuildClient(host.BaseUrl);

        var result = await client.DownloadImageAsync(Token, "missing-file-id", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadImageAsync_WhenGetFileReturns500_Throws()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getFile", () => Results.StatusCode(StatusCodes.Status500InternalServerError)));
        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DownloadImageAsync(Token, "abc-file-id", CancellationToken.None));
    }

    [Fact]
    public async Task DownloadImageAsync_WhenTheFileDownloadItselfRefusesWith404_ReturnsNull()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
        {
            app.MapGet($"/bot{Token}/getFile", () =>
                Results.Json(new { ok = true, result = new { file_path = "photos/gone.jpg" } }));
            app.MapGet($"/file/bot{Token}/photos/gone.jpg", () => Results.StatusCode(StatusCodes.Status404NotFound));
        });
        var client = BuildClient(host.BaseUrl);

        var result = await client.DownloadImageAsync(Token, "abc-file-id", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadImageAsync_WhenTheFileDownloadItselfReturns500_Throws()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
        {
            app.MapGet($"/bot{Token}/getFile", () =>
                Results.Json(new { ok = true, result = new { file_path = "photos/broken.jpg" } }));
            app.MapGet(
                $"/file/bot{Token}/photos/broken.jpg",
                () => Results.StatusCode(StatusCodes.Status500InternalServerError));
        });
        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DownloadImageAsync(Token, "abc-file-id", CancellationToken.None));
    }

    private static TelegramApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for Telegram's own
    /// API - <see cref="ForwardedHeadersTests"/>' own established technique in this project, reused
    /// here for a channel provider's HTTP boundary instead of an ingress concern.</summary>
    private static async Task<TestHost> BuildFakeTelegramHostAsync(Action<WebApplication> configureRoutes)
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
