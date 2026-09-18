using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-152`: <see cref="TelegramChannelAdapter.SendAsync"/>'s own new responsibility -
/// <see cref="OutboundChannelMessage.RequestContactIfSupported"/> reaching Telegram's real wire shape as
/// a <c>ReplyKeyboardMarkup</c> with a <c>request_contact</c> button. Uses the identical minimal
/// fake-repository/fake-host technique <see cref="VkChannelAdapterTests"/> already establishes; the
/// terminal/transient split and every routing case this adapter shared with `14-07` before this item are
/// <see cref="TelegramApiClientTests"/>'s own scope (the wire client) and were never duplicated here.
/// </summary>
public sealed class TelegramChannelAdapterTests
{
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private const string Token = "123456:test-token-not-a-real-secret";

    private static OutboundChannelMessage Reply(bool requestContact, string recipient = "42") => new(
        ChannelKind.Telegram, new ExternalChannelAddress(recipient), ConversationId, new MessageId(Guid.NewGuid()),
        new MessageBody("What is your phone number?"), requestContact);

    /// <summary>Done-when 1: the phone step's own flag reaches a Telegram visitor as a working
    /// <c>request_contact</c> reply-keyboard button, alongside the unchanged prose prompt - proven
    /// through the whole adapter, not just <see cref="TelegramApiClient"/> in isolation.</summary>
    [Fact]
    public async Task SendAsync_WhenRequestContactIfSupportedIsSet_SendsAReplyKeyboardWithARequestContactButton()
    {
        string? capturedBody = null;
        await using var fakeTelegram = await BuildFakeTelegramHostAsync(async (HttpContext ctx) =>
        {
            capturedBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            return Results.Json(new { ok = true, result = new { message_id = 1 } });
        });
        var adapter = BuildAdapter(fakeTelegram.BaseUrl);

        var outcome = await adapter.SendAsync(Reply(requestContact: true), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.NotNull(capturedBody);
        using var document = System.Text.Json.JsonDocument.Parse(capturedBody);
        Assert.Equal("What is your phone number?", document.RootElement.GetProperty("text").GetString());
        var button = document.RootElement.GetProperty("reply_markup").GetProperty("keyboard")[0][0];
        Assert.True(button.GetProperty("request_contact").GetBoolean());
    }

    /// <summary>Done-when 4's own mirror for Telegram itself: an ordinary reply
    /// (<see cref="OutboundChannelMessage.RequestContactIfSupported"/> unset, its default) must not grow
    /// a <c>reply_markup</c> field at all - byte-identical to what this adapter has always sent.</summary>
    [Fact]
    public async Task SendAsync_WhenRequestContactIfSupportedIsNotSet_SendsNoReplyMarkupField()
    {
        string? capturedBody = null;
        await using var fakeTelegram = await BuildFakeTelegramHostAsync(async (HttpContext ctx) =>
        {
            capturedBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            return Results.Json(new { ok = true, result = new { message_id = 1 } });
        });
        var adapter = BuildAdapter(fakeTelegram.BaseUrl);

        var outcome = await adapter.SendAsync(Reply(requestContact: false), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("reply_markup", capturedBody);
    }

    private static TelegramChannelAdapter BuildAdapter(string telegramBaseUrl)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConversationRepository>(_ => new FixedConversationRepository());
        services.AddScoped<IChannelCredentialRepository>(_ => new FixedChannelCredentialRepository());
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        var provider = services.BuildServiceProvider();

        var httpClient = new HttpClient { BaseAddress = new Uri(telegramBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var apiClient = new TelegramApiClient(httpClient);

        return new TelegramChannelAdapter(apiClient, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TelegramChannelAdapter>.Instance);
    }

    private sealed record FakeTelegramHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<FakeTelegramHost> BuildFakeTelegramHostAsync(Func<HttpContext, Task<IResult>> respond)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost($"/bot{Token}/sendMessage", respond);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new FakeTelegramHost(app, addresses.First() + "/");
    }

    private sealed class FixedConversationRepository : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult<Conversation?>(Conversation.Start(id, SiteId, new VisitorId(Guid.NewGuid()), DateTimeOffset.UtcNow));

        public Task<IReadOnlyDictionary<ConversationId, Conversation>> GetByIdsAsync(
            IReadOnlyCollection<ConversationId> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<ConversationId, Conversation>>(
                ids.ToDictionary(id => id, id => Conversation.Start(id, SiteId, new VisitorId(Guid.NewGuid()), DateTimeOffset.UtcNow)));

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedChannelCredentialRepository : IChannelCredentialRepository
    {
        public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
            Task.FromResult<ChannelCredential?>(ChannelCredential.Register(
                new ChannelCredentialId(Guid.NewGuid()), siteId, kind, [1, 2, 3], [4, 5, 6], DateTimeOffset.UtcNow, null));

        public Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChannelCredential?> GetActiveByProviderAccountIdAsync(
            ChannelKind kind, string providerAccountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PassthroughCipher : IChannelCredentialCipher
    {
        public byte[] Encrypt(string token) => System.Text.Encoding.UTF8.GetBytes(token);

        public string Decrypt(byte[] ciphertext) => Token;
    }
}
