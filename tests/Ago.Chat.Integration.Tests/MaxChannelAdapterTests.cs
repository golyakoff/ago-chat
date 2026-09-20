using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.MaxBot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-152`: <see cref="MaxChannelAdapter.SendAsync"/>'s own new responsibility -
/// <see cref="OutboundChannelMessage.RequestContactIfSupported"/> reaching MAX's own wire shape as an
/// <c>inline_keyboard</c> attachment with a <c>request_contact</c>-type button. Uses the identical
/// minimal fake-repository/in-process-host technique <see cref="VkChannelAdapterTests"/> already
/// establishes, deliberately lighter than <see cref="MaxChannelAdapterResilienceTests"/>'s own separate
/// <c>Ago.Chat.FakeMax</c> process - that fixture exists to prove the resilience pipeline survives a real
/// killed process, which this item's own scope (a button shape, not a failure mode) does not need.
///
/// <para><b>The one honest caveat this file cannot remove.</b> Every assertion here is against this
/// item's own <see cref="MaxOutboundAttachment"/> reconstruction of MAX's documented outline
/// (<see cref="MaxApiClient"/>'s own remarks) - it proves the code sends what it was built to send, not
/// that a real MAX bot accepts or renders it. No live MAX bot or token was available while this item was
/// built; this is stated in the item's own report as the standing Done-when exception, following
/// `25-151`'s own precedent for its own inbound half.</para>
/// </summary>
public sealed class MaxChannelAdapterTests
{
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private const string Token = "max-test-token-not-a-real-secret";

    private static OutboundChannelMessage Reply(bool requestContact, string recipient = "42") => new(
        ChannelKind.Max, new ExternalChannelAddress(recipient), ConversationId, new MessageId(Guid.NewGuid()),
        new MessageBody("What is your phone number?"), requestContact);

    /// <summary>Done-when 2: the phone step's own flag reaches a MAX visitor as a working
    /// <c>request_contact</c> inline button, alongside the unchanged prose prompt - proven only against
    /// this item's own documented-shape reconstruction, explicitly not against a real MAX bot (this
    /// class's own remarks).</summary>
    [Fact]
    public async Task SendAsync_WhenRequestContactIfSupportedIsSet_SendsAnInlineKeyboardWithARequestContactButton()
    {
        string? capturedBody = null;
        await using var fakeMax = await BuildFakeMaxHostAsync(async (HttpContext ctx) =>
        {
            capturedBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            return Results.Json(new { message = new { body = new { mid = "1" } } });
        });
        var adapter = BuildAdapter(fakeMax.BaseUrl);

        var outcome = await adapter.SendAsync(Reply(requestContact: true), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.NotNull(capturedBody);
        using var document = System.Text.Json.JsonDocument.Parse(capturedBody);
        Assert.Equal("What is your phone number?", document.RootElement.GetProperty("text").GetString());
        var attachment = document.RootElement.GetProperty("attachments")[0];
        Assert.Equal("inline_keyboard", attachment.GetProperty("type").GetString());
        var button = attachment.GetProperty("payload").GetProperty("buttons")[0][0];
        Assert.Equal("request_contact", button.GetProperty("type").GetString());
    }

    /// <summary>Done-when 4's own mirror for MAX itself: an ordinary reply
    /// (<see cref="OutboundChannelMessage.RequestContactIfSupported"/> unset, its default) must not grow
    /// an <c>attachments</c> field at all - byte-identical to what this adapter has always sent.</summary>
    [Fact]
    public async Task SendAsync_WhenRequestContactIfSupportedIsNotSet_SendsNoAttachmentsField()
    {
        string? capturedBody = null;
        await using var fakeMax = await BuildFakeMaxHostAsync(async (HttpContext ctx) =>
        {
            capturedBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            return Results.Json(new { message = new { body = new { mid = "1" } } });
        });
        var adapter = BuildAdapter(fakeMax.BaseUrl);

        var outcome = await adapter.SendAsync(Reply(requestContact: false), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("attachments", capturedBody);
    }

    private static MaxChannelAdapter BuildAdapter(string maxBaseUrl)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConversationRepository>(_ => new FixedConversationRepository());
        services.AddScoped<IChannelCredentialRepository>(_ => new FixedChannelCredentialRepository());
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        var provider = services.BuildServiceProvider();

        var httpClient = new HttpClient { BaseAddress = new Uri(maxBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var apiClient = new MaxApiClient(httpClient);

        return new MaxChannelAdapter(apiClient, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<MaxChannelAdapter>.Instance);
    }

    private sealed record FakeMaxHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<FakeMaxHost> BuildFakeMaxHostAsync(Func<HttpContext, Task<IResult>> respond)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/messages", respond);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new FakeMaxHost(app, addresses.First() + "/");
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

        public Task ReloadAsync(ChannelCredential credential, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PassthroughCipher : IChannelCredentialCipher
    {
        public byte[] Encrypt(string token) => System.Text.Encoding.UTF8.GetBytes(token);

        public string Decrypt(byte[] ciphertext) => Token;
    }
}
