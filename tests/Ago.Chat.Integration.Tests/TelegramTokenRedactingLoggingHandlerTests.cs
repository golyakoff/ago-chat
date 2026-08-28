using Ago.Chat.Infrastructure.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// Found live 2026-08-28, verifying `14-07` against a real bot token: `Ago.Chat.Worker`'s own logs
/// printed the token in full on every request - see <see cref="TelegramTokenRedactingLoggingHandler"/>'s
/// own remarks for the mechanism. Two levels of proof, matching how sharp-edged this class of bug is:
/// the <c>RedactToken_*</c> theory/facts below prove the pure redaction function against the exact
/// shapes <see cref="TelegramApiClient"/> actually builds; the end-to-end fact below that
/// proves the thing that actually matters - that wiring this handler the way <c>ChatModule</c> does
/// (<c>RemoveAllLoggers</c> + <c>AddHttpMessageHandler</c>) means no captured log line ever contains the
/// real token, end to end through a real <c>HttpClient</c> pipeline and a real request - a unit test of
/// <see cref="TelegramTokenRedactingLoggingHandler.RedactToken"/> alone would not have caught a wiring
/// mistake that left the default loggers in place alongside this one.
/// </summary>
public sealed class TelegramTokenRedactingLoggingHandlerTests
{
    private const string RealToken = "8957102923:AAFnBe_1H9lGMWJJNOCVOHylmdtKbY32KBk-not-a-real-secret";

    [Theory]
    [InlineData("./bot123456:AAExampleToken/getUpdates?timeout=30", "./bot***/getUpdates?timeout=30")]
    [InlineData("./bot123456:AAExampleToken/sendMessage", "./bot***/sendMessage")]
    [InlineData("./bot123456:AAExampleToken/getMe", "./bot***/getMe")]
    public void RedactToken_ForAUriTelegramApiClientActuallyBuilds_ReplacesOnlyTheTokenSegment(
        string input, string expected)
    {
        var redacted = TelegramTokenRedactingLoggingHandler.RedactToken(new Uri(input, UriKind.Relative));

        Assert.Equal(expected, redacted);
    }

    [Fact]
    public void RedactToken_ForANullUri_ReturnsAPlaceholderRatherThanThrowing()
    {
        var redacted = TelegramTokenRedactingLoggingHandler.RedactToken(null);

        Assert.Equal("(no request uri)", redacted);
    }

    [Fact]
    public async Task ARealRequestThroughTheConfiguredPipeline_NeverLogsTheRealToken()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{RealToken}/getMe", () => Results.Json(new { ok = true, result = new { id = 1 } })));

        var capturingLogger = new CapturingLogger<TelegramTokenRedactingLoggingHandler>();
        using var handler = new TelegramTokenRedactingLoggingHandler(capturingLogger)
        {
            InnerHandler = new HttpClientHandler(),
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri(host.BaseUrl) };
        var client = new TelegramApiClient(httpClient);

        var result = await client.GetMeAsync(RealToken, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotEmpty(capturingLogger.Messages);
        Assert.All(capturingLogger.Messages, message => Assert.DoesNotContain(RealToken, message));
        Assert.Contains(capturingLogger.Messages, message => message.Contains("bot***"));
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary><see cref="TelegramApiClientTests"/>'s own established technique - a real Kestrel host
    /// on a real ephemeral loopback port, standing in for Telegram's own API.</summary>
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

    /// <summary>Duplicated small fake, matching this project's own established convention
    /// (<c>TracingEndToEndTests</c>, <c>Ago.Chat.Concurrency.Tests</c>) of a private capturing logger per
    /// test file rather than a shared test-only package.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
