using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.WhatsApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-10`: <see cref="WhatsAppChannelAdapter.SendAsync"/>'s own routing/resolution logic - the parts
/// specific to this adapter rather than the generic resilience wrapping around it
/// (<see cref="ResilientInboundChannelAdapterTests"/>'s own scope) or WhatsApp's real HTTP error shape
/// (<see cref="WhatsAppApiClientTests"/>'s own scope). Uses the identical minimal fake-repository
/// technique <see cref="VkChannelAdapterTests"/> already establishes.
/// </summary>
public sealed class WhatsAppChannelAdapterTests
{
    private const string PhoneNumberId = "106540352242922";
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static OutboundChannelMessage Reply(Guid messageId, string recipient = "16505551234") => new(
        ChannelKind.WhatsApp, new ExternalChannelAddress(recipient), ConversationId, new MessageId(messageId),
        new MessageBody("an operator's answer"));

    [Fact]
    public async Task SendAsync_WhenWhatsAppAnswers_ReturnsSentWithTheProviderMessageId()
    {
        await using var fakeWhatsApp = await BuildFakeWhatsAppHostAsync(() => Results.Json(
            new { messages = new[] { new { id = "wamid.abc123" } } }));
        var adapter = BuildAdapter(fakeWhatsApp.BaseUrl, providerAccountId: PhoneNumberId);

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Equal("wamid.abc123", outcome.ProviderMessageId);
    }

    [Fact]
    public async Task SendAsync_WhenNoActiveCredentialExists_ReturnsRefused()
    {
        await using var fakeWhatsApp = await BuildFakeWhatsAppHostAsync(() => Results.Json(new { messages = Array.Empty<object>() }));
        var adapter = BuildAdapter(fakeWhatsApp.BaseUrl, providerAccountId: PhoneNumberId, hasActiveCredential: false);

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("No active WhatsApp number", outcome.FailureReason);
    }

    [Fact]
    public async Task SendAsync_WhenTheCredentialHasNoProviderAccountId_Throws()
    {
        await using var fakeWhatsApp = await BuildFakeWhatsAppHostAsync(() => Results.Json(new { messages = Array.Empty<object>() }));
        var adapter = BuildAdapter(fakeWhatsApp.BaseUrl, providerAccountId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None));
    }

    // No "empty recipient" test the way VkChannelAdapterTests' own non-numeric-recipient test exists -
    // WhatsAppChannelAdapter's own remarks explain why: Domain.ExternalChannelAddress already refuses an
    // empty value at construction (proven by that type's own Ago.Chat.Domain.Tests coverage), so there is
    // no reachable state left in this adapter for a test here to exercise.

    /// <summary>`131047` surfaced through the whole adapter, not just <see cref="WhatsAppApiClient"/> in
    /// isolation - proving the 24-hour-window refusal reaches an operator as an ordinary
    /// <see cref="ChannelSendOutcome.Refused"/>, the exact claim <see cref="WhatsAppChannelAdapter"/>'s own
    /// remarks make about respecting the constraint without building template machinery.</summary>
    [Fact]
    public async Task SendAsync_WhenWhatsAppRefusesOutsideThe24HourWindow_ReturnsRefused()
    {
        await using var fakeWhatsApp = await BuildFakeWhatsAppHostAsync(() => Results.Json(
            new { error = new { message = "more than 24 hours have passed since the recipient last replied", type = "OAuthException", code = 131047 } },
            statusCode: 400));
        var adapter = BuildAdapter(fakeWhatsApp.BaseUrl, providerAccountId: PhoneNumberId);

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("131047", outcome.FailureReason);
    }

    private static WhatsAppChannelAdapter BuildAdapter(string whatsAppBaseUrl, string? providerAccountId, bool hasActiveCredential = true)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConversationRepository>(_ => new FixedConversationRepository());
        services.AddScoped<IChannelCredentialRepository>(_ => new FixedChannelCredentialRepository(hasActiveCredential, providerAccountId));
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        var provider = services.BuildServiceProvider();

        var httpClient = new HttpClient { BaseAddress = new Uri(whatsAppBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var apiClient = new WhatsAppApiClient(httpClient);

        return new WhatsAppChannelAdapter(apiClient, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<WhatsAppChannelAdapter>.Instance);
    }

    private sealed record FakeWhatsAppHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<FakeWhatsAppHost> BuildFakeWhatsAppHostAsync(Func<IResult> respond)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost($"/{PhoneNumberId}/messages", respond);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new FakeWhatsAppHost(app, addresses.First() + "/");
    }

    private sealed class FixedConversationRepository : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult<Conversation?>(Conversation.Start(id, SiteId, new VisitorId(Guid.NewGuid()), DateTimeOffset.UtcNow));

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedChannelCredentialRepository(bool hasActiveCredential, string? providerAccountId) : IChannelCredentialRepository
    {
        public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(hasActiveCredential
                ? ChannelCredential.Register(
                    new ChannelCredentialId(Guid.NewGuid()), siteId, kind, [1, 2, 3], [4, 5, 6], DateTimeOffset.UtcNow, providerAccountId)
                : null);

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

        public string Decrypt(byte[] ciphertext) => "fake-token-not-a-real-secret";
    }
}
