using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Api.Channels;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.Vk;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-08`: the production `VkWebhookEndpoints.MapVkWebhookEndpoints` mapping - a real Kestrel host on a
/// real ephemeral loopback port, standing in for VK's own Callback API delivery
/// (<see cref="YooKassaWebhookEndpointTests"/>'s own established technique for a real-external-webhook
/// receiver), against a real Postgres (<see cref="PostgresFixture"/>) and a real
/// <see cref="ReceiveChannelMessageHandler"/> chain (<see cref="ReceiveChannelMessageDrainedByTheRealPipelineTests"/>'s
/// own technique for wiring that handler with <see cref="SynchronousMessagePipeline"/> rather than
/// standing up the full worker/flusher stack for a test about something other than the pipeline's own
/// batching).
///
/// <para><b>This is this item's own proof for two separate Done-when requirements at once:</b> the
/// confirmation handshake (VK's own <c>type: "confirmation"</c> event must get the exact code
/// <see cref="VkApiClient.GetCallbackConfirmationCodeAsync"/> would fetch, back as the entire plain-text
/// response body), and a real message reaching an operator through the real handler chain, the same bar
/// <c>MaxInboundMessageParserTests</c>'s own sibling test class holds itself to (though there, unlike
/// here, the webhook route itself was never exercised, because MAX's webhook is one of two inbound
/// mechanisms and this item found no dedicated MAX/Telegram precedent test for the HTTP route itself -
/// this item's own backlog explicitly asks for the confirmation handshake "proven by a test", which is
/// what makes a real HTTP-level test worth building here specifically).</para>
///
/// <para>What this does <b>not</b> prove: that a real VK community's own Callback API delivery actually
/// reaches this endpoint over the public internet, or that the field names this item assumed for
/// <c>message_new</c>'s own <c>object.message</c> nesting are VK's true shape - <c>VkDtos.cs</c>'s own
/// honesty note, and this item's own report, say plainly why that remains unverified in this
/// environment.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VkWebhookEndpointsTests(PostgresFixture fixture)
{
    private const string CorrectSecret = "correct-webhook-secret";
    private const string PlaintextToken = "test-vk-community-token-not-a-real-secret";
    private const long GroupId = 555555;
    private const string FakeConfirmationCode = "a1b2c3d4";

    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Confirmation_WithTheCorrectSecret_ReturnsTheConfirmationCodeAsPlainText()
    {
        var (_, credentialId) = await SeedActiveCredentialAsync();
        await using var fakeVk = await BuildFakeVkHostAsync();
        await using var host = await BuildHostAsync(fakeVk.BaseUrl);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/vk/{credentialId.Value}",
            JsonBody(new { type = "confirmation", group_id = GroupId, secret = CorrectSecret }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeConfirmationCode, await response.Content.ReadAsStringAsync());
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Confirmation_WithAWrongSecret_Returns401()
    {
        var (_, credentialId) = await SeedActiveCredentialAsync();
        await using var fakeVk = await BuildFakeVkHostAsync();
        await using var host = await BuildHostAsync(fakeVk.BaseUrl);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/vk/{credentialId.Value}",
            JsonBody(new { type = "confirmation", group_id = GroupId, secret = "a-completely-wrong-secret" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnyEvent_ForAnUnknownCredentialId_Returns404()
    {
        await using var fakeVk = await BuildFakeVkHostAsync();
        await using var host = await BuildHostAsync(fakeVk.BaseUrl);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/vk/{Guid.NewGuid()}",
            JsonBody(new { type = "confirmation", group_id = GroupId, secret = CorrectSecret }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Info-hiding, the same as <c>MaxWebhookEndpoints</c>' own precedent: a revoked
    /// credential's own URL reads exactly like one that was never registered.</summary>
    [Fact]
    public async Task AnyEvent_ForARevokedCredential_Returns404()
    {
        var (_, credentialId) = await SeedActiveCredentialAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            var credential = await repository.GetByIdAsync(credentialId, CancellationToken.None);
            credential!.Revoke();
            await repository.SaveAsync(credential, CancellationToken.None);
        }

        await using var fakeVk = await BuildFakeVkHostAsync();
        await using var host = await BuildHostAsync(fakeVk.BaseUrl);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/vk/{credentialId.Value}",
            JsonBody(new { type = "confirmation", group_id = GroupId, secret = CorrectSecret }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MessageNew_WithTheCorrectSecret_ReturnsOk_AndTheMessageReachesPostgresThroughTheRealHandlerChain()
    {
        var (siteId, credentialId) = await SeedActiveCredentialAsync();
        await using var fakeVk = await BuildFakeVkHostAsync();
        await using var host = await BuildHostAsync(fakeVk.BaseUrl);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/vk/{credentialId.Value}",
            JsonBody(new
            {
                type = "message_new",
                group_id = GroupId,
                secret = CorrectSecret,
                event_id = "evt-1",
                @object = new
                {
                    message = new Dictionary<string, object?>
                    {
                        ["id"] = 42,
                        ["date"] = 1_700_000_000,
                        ["from_id"] = 194525157,
                        ["peer_id"] = 194525157,
                        ["text"] = "hello from a real VK community",
                        ["out"] = 0,
                    },
                },
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());

        var body = await ReadMessageBodyAsync(siteId);
        Assert.Equal("hello from a real VK community", body);
    }

    /// <summary>The one rule with no MAX/Telegram equivalent, proven end to end through the real HTTP
    /// route - <c>VkInboundMessageParser</c>'s own remarks on why a community's own outgoing echo must
    /// never reach <see cref="ReceiveChannelMessageHandler"/> at all.</summary>
    [Fact]
    public async Task MessageNew_ForTheCommunitysOwnOutgoingEcho_ReturnsOk_ButWritesNothingToTheDatabase()
    {
        var (siteId, credentialId) = await SeedActiveCredentialAsync();
        await using var fakeVk = await BuildFakeVkHostAsync();
        await using var host = await BuildHostAsync(fakeVk.BaseUrl);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/vk/{credentialId.Value}",
            JsonBody(new
            {
                type = "message_new",
                group_id = GroupId,
                secret = CorrectSecret,
                event_id = "evt-2",
                @object = new
                {
                    message = new Dictionary<string, object?>
                    {
                        ["id"] = 43,
                        ["date"] = 1_700_000_001,
                        ["from_id"] = GroupId,
                        ["peer_id"] = 194525157,
                        ["text"] = "an operator's own reply, echoed back by VK",
                        ["out"] = 1,
                    },
                },
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    [Fact]
    public async Task AnUnrecognizedEventType_ReturnsOk_WithoutTouchingTheDatabase()
    {
        var (siteId, credentialId) = await SeedActiveCredentialAsync();
        await using var fakeVk = await BuildFakeVkHostAsync();
        await using var host = await BuildHostAsync(fakeVk.BaseUrl);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/vk/{credentialId.Value}",
            JsonBody(new { type = "wall_post_new", group_id = GroupId, secret = CorrectSecret, @object = new { } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    private static StringContent JsonBody(object payload) =>
        new(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private async Task<string?> ReadMessageBodyAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT m.body FROM messages m JOIN conversations c ON c.id = m.conversation_id WHERE c.site_id = @siteId", connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<(SiteId SiteId, ChannelCredentialId CredentialId)> SeedActiveCredentialAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var credentialId = new ChannelCredentialId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync(CancellationToken.None);

        var webhookSecretHash = SHA256.HashData(Encoding.UTF8.GetBytes(CorrectSecret));
        var credential = ChannelCredential.Register(
            credentialId, siteId, ChannelKind.Vk, Cipher.Encrypt(PlaintextToken), webhookSecretHash, Now,
            providerAccountId: GroupId.ToString());

        var repository = new ChannelCredentialRepository(db);
        await repository.SaveAsync(credential, CancellationToken.None);

        return (siteId, credentialId);
    }

    // One cipher instance per test method (xUnit constructs a fresh VkWebhookEndpointsTests per [Fact]),
    // so the key used to encrypt the seeded token in SeedActiveCredentialAsync is the identical key
    // BuildHostAsync's own DI registration decrypts with - a mismatched pair would make every
    // Confirmation/MessageNew test fail with a decryption error rather than the assertion actually under
    // test.
    private ChannelCredentialCipher? _cipher;

    private ChannelCredentialCipher Cipher => _cipher ??=
        new ChannelCredentialCipher(new ChannelCredentialCipherOptions
        {
            CredentialEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });

    private sealed record FakeVkHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>Stands in for VK's own API, for exactly the one call this endpoint ever makes to it -
    /// <c>groups.getCallbackConfirmationCode</c>, during the confirmation handshake - the same
    /// in-process ephemeral-Kestrel technique <see cref="VkApiClientTests"/> already establishes for
    /// this provider.</summary>
    private static async Task<FakeVkHost> BuildFakeVkHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/groups.getCallbackConfirmationCode", () => Results.Json(new { response = new { code = FakeConfirmationCode } }));

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new FakeVkHost(app, addresses.First() + "/");
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private async Task<TestHost> BuildHostAsync(string fakeVkBaseUrl)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddScoped<IChannelCredentialRepository, ChannelCredentialRepository>();
        builder.Services.AddScoped<IChannelCredentialCipher>(_ => Cipher);
        builder.Services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
        builder.Services.AddScoped<IVisitorRepository, VisitorRepository>();
        builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
        builder.Services.AddScoped<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton(new MessageSendRateLimitOptions());
        builder.Services.AddSingleton<IMessagePipeline>(_ => new SynchronousMessagePipeline(fixture.DataSource));
        builder.Services.AddScoped<StartConversationHandler>();
        builder.Services.AddScoped<SendVisitorMessageHandler>();
        builder.Services.AddScoped<ReceiveChannelMessageHandler>();
        // A named client, not AddHttpClient&lt;VkApiClient&gt;() - ChatModule's own identical registration
        // has the full reasoning (VkApiClient's second constructor parameter is not itself a DI service).
        builder.Services.AddHttpClient(nameof(VkApiClient), (_, client) => client.BaseAddress = new Uri(fakeVkBaseUrl));
        builder.Services.AddSingleton(sp =>
            new VkApiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(VkApiClient)), "5.199"));

        var app = builder.Build();

        // The real production mapping - no duplicated route or handler logic, the same discipline
        // YooKassaWebhookEndpointTests' own BuildHostAsync states for itself.
        app.MapVkWebhookEndpoints();

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }
}
