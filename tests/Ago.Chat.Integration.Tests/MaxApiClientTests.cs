using Ago.Chat.Infrastructure.MaxBot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-147`: <see cref="MaxApiClient.GetMeAsync"/>'s own terminal/transient split, proven against a real
/// HTTP boundary the identical lightweight way <see cref="TelegramApiClientTests"/> already established
/// for Telegram's own <c>getMe</c> - a real (in-process, ephemeral-port) Kestrel host, not the separate
/// <c>Ago.Chat.FakeMax</c> process <see cref="MaxChannelAdapterResilienceTests"/> stands up for the
/// resilience-pipeline proof that call already has. This one new method needs only a real HTTP boundary
/// that can answer different status codes, which the smaller fixture gives for a fraction of the
/// ceremony - <see cref="TelegramApiClientTests"/>'s own remarks give the fuller version of this same
/// reasoning.
/// </summary>
public sealed class MaxApiClientTests
{
    private const string Token = "max-test-token-not-a-real-secret";

    [Fact]
    public async Task GetMeAsync_WhenMaxAnswersOk_ReturnsTheBotsOwnUsername()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.Json(new { user_id = 1, is_bot = true, username = "shop_support_bot" })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("shop_support_bot", result.Username);
    }

    [Fact]
    public async Task GetMeAsync_WhenMaxAnswersOkWithNoUsername_ReturnsNullUsername()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.Json(new { user_id = 1, is_bot = true })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.Username);
    }

    /// <summary>`25-152`: <c>requestContact: false</c> (every ordinary reply) must not add an
    /// <c>attachments</c> field at all - the identical "byte-identical to today" proof
    /// <see cref="TelegramApiClientTests"/>'s own equivalent test makes for Telegram's
    /// <c>reply_markup</c>. See <see cref="MaxApiClient"/>'s own remarks for the standing caveat that
    /// this shape is built from MAX's documented outline, not a live capture.</summary>
    [Fact]
    public async Task SendMessageAsync_WithRequestContactFalse_SendsNoAttachmentsField()
    {
        string? capturedBody = null;
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapPost("/messages", async (HttpContext ctx) =>
            {
                capturedBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
                return Results.Json(new { message = new { body = new { mid = "1" } } });
            }));
        var client = BuildClient(host.BaseUrl);

        await client.SendMessageAsync(Token, chatId: 42, text: "hello", requestContact: false, CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("attachments", capturedBody);
    }

    /// <summary>`25-152`'s own outbound half: a phone-collection step's own flag reaches MAX as an
    /// <c>inline_keyboard</c> attachment carrying exactly one <c>request_contact</c>-type button,
    /// alongside the unchanged prose text - proven against the real wire body, against the documented
    /// shape only (<see cref="MaxApiClient"/>'s own remarks: no live MAX bot was available to confirm
    /// this envelope).</summary>
    [Fact]
    public async Task SendMessageAsync_WithRequestContactTrue_SendsAnInlineKeyboardWithARequestContactButton()
    {
        string? capturedBody = null;
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapPost("/messages", async (HttpContext ctx) =>
            {
                capturedBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
                return Results.Json(new { message = new { body = new { mid = "1" } } });
            }));
        var client = BuildClient(host.BaseUrl);

        await client.SendMessageAsync(
            Token, chatId: 42, text: "What is your phone number?", requestContact: true, CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var document = System.Text.Json.JsonDocument.Parse(capturedBody);
        var attachments = document.RootElement.GetProperty("attachments");
        var attachment = Assert.Single(attachments.EnumerateArray());
        Assert.Equal("inline_keyboard", attachment.GetProperty("type").GetString());
        var firstRow = attachment.GetProperty("payload").GetProperty("buttons")[0];
        Assert.Single(firstRow.EnumerateArray());
        Assert.Equal("request_contact", firstRow[0].GetProperty("type").GetString());
    }

    /// <summary>401 is this item's own reasoned default for "MAX refused the token" - a terminal
    /// refusal, never worth retrying, so it must come back as a result the caller inspects rather than an
    /// exception - the same shape <see cref="MaxApiClient.SendMessageAsync"/> already gives for the
    /// identical status code.</summary>
    [Fact]
    public async Task GetMeAsync_WhenMaxRejectsWith401_ReturnsRefused()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized)));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.Username);
        Assert.Contains("401", result.RefusalReason);
    }

    /// <summary>A server-shaped error is transient, never a refusal - the identical
    /// "client-shaped errors are refusals, server-shaped errors are transient" default this client's own
    /// remarks state for <see cref="MaxApiClient.SendMessageAsync"/>.</summary>
    [Fact]
    public async Task GetMeAsync_WhenMaxReturns500_Throws()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.StatusCode(StatusCodes.Status500InternalServerError)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMeAsync(Token, CancellationToken.None));
    }

    private static MaxApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<TestHost> BuildFakeMaxHostAsync(Action<WebApplication> configureRoutes)
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
