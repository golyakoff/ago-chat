using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Vk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-08`: <see cref="VkChannelAdapter.SendAsync"/>'s own routing/resolution logic - the parts specific
/// to this adapter rather than the generic resilience wrapping around it
/// (<see cref="ResilientInboundChannelAdapterTests"/>'s own scope) or VK's real HTTP error shape
/// (<see cref="VkApiClientTests"/>'s own scope). Uses the same minimal fake-repository technique
/// <see cref="MaxChannelAdapterResilienceTests"/> already establishes - the thing under test is this
/// class's own credential/group-id resolution, not the repository layer
/// (<see cref="ChannelCredentialRepositoryTests"/> proves that separately, against a real Postgres).
/// </summary>
public sealed class VkChannelAdapterTests
{
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static OutboundChannelMessage Reply(Guid messageId, string recipient = "194525157") => new(
        ChannelKind.Vk, new ExternalChannelAddress(recipient), ConversationId, new MessageId(messageId),
        new MessageBody("an operator's answer"));

    [Fact]
    public async Task SendAsync_WhenVkAnswers_ReturnsSentWithTheProviderMessageId()
    {
        await using var fakeVk = await BuildFakeVkHostAsync(() => Results.Json(new { response = 555 }));
        var adapter = BuildAdapter(fakeVk.BaseUrl, providerAccountId: "1");

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Equal("555", outcome.ProviderMessageId);
    }

    [Fact]
    public async Task SendAsync_WhenNoActiveCredentialExists_ReturnsRefused()
    {
        await using var fakeVk = await BuildFakeVkHostAsync(() => Results.Json(new { response = 1 }));
        var adapter = BuildAdapter(fakeVk.BaseUrl, providerAccountId: "1", hasActiveCredential: false);

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("No active VK community", outcome.FailureReason);
    }

    [Fact]
    public async Task SendAsync_WhenTheCredentialHasNoProviderAccountId_Throws()
    {
        await using var fakeVk = await BuildFakeVkHostAsync(() => Results.Json(new { response = 1 }));
        var adapter = BuildAdapter(fakeVk.BaseUrl, providerAccountId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_WithANonNumericRecipient_ReturnsRefused()
    {
        await using var fakeVk = await BuildFakeVkHostAsync(() => Results.Json(new { response = 1 }));
        var adapter = BuildAdapter(fakeVk.BaseUrl, providerAccountId: "1");

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid(), recipient: "not-a-vk-peer-id"), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("not a VK peer id", outcome.FailureReason);
    }

    /// <summary>VK's own idempotency key for <c>messages.send</c> - <see cref="VkChannelAdapter"/>'s own
    /// remarks on why this must be derived deterministically from <see cref="OutboundChannelMessage.MessageId"/>:
    /// a resilience-pipeline retry of the identical message must reach VK with the identical
    /// <c>random_id</c>, or VK's own deduplication does nothing and a retried send becomes a second,
    /// visible message.</summary>
    [Fact]
    public async Task SendAsync_CalledTwiceWithTheSameMessageId_SendsTheIdenticalRandomIdBothTimes()
    {
        var capturedRandomIds = new List<string?>();
        await using var fakeVk = await BuildFakeVkHostAsync(async httpContext =>
        {
            var form = await httpContext.Request.ReadFormAsync();
            capturedRandomIds.Add(form["random_id"]);
            return Results.Json(new { response = 1 });
        });
        var adapter = BuildAdapter(fakeVk.BaseUrl, providerAccountId: "1");
        var messageId = Guid.NewGuid();

        await adapter.SendAsync(Reply(messageId), CancellationToken.None);
        await adapter.SendAsync(Reply(messageId), CancellationToken.None);

        Assert.Equal(2, capturedRandomIds.Count);
        Assert.Equal(capturedRandomIds[0], capturedRandomIds[1]);
        Assert.False(string.IsNullOrEmpty(capturedRandomIds[0]));
    }

    private static VkChannelAdapter BuildAdapter(string vkBaseUrl, string? providerAccountId, bool hasActiveCredential = true)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConversationRepository>(_ => new FixedConversationRepository());
        services.AddScoped<IChannelCredentialRepository>(_ => new FixedChannelCredentialRepository(hasActiveCredential, providerAccountId));
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        var provider = services.BuildServiceProvider();

        var httpClient = new HttpClient { BaseAddress = new Uri(vkBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var apiClient = new VkApiClient(httpClient, "5.199");

        return new VkChannelAdapter(apiClient, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<VkChannelAdapter>.Instance);
    }

    private sealed record FakeVkHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<FakeVkHost> BuildFakeVkHostAsync(Func<IResult> respond) =>
        await BuildFakeVkHostAsync(_ => Task.FromResult(respond()));

    private static async Task<FakeVkHost> BuildFakeVkHostAsync(Func<HttpContext, Task<IResult>> respond)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/messages.send", respond);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new FakeVkHost(app, addresses.First() + "/");
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

        public Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PassthroughCipher : IChannelCredentialCipher
    {
        public byte[] Encrypt(string token) => System.Text.Encoding.UTF8.GetBytes(token);

        public string Decrypt(byte[] ciphertext) => "fake-token-not-a-real-secret";
    }
}
