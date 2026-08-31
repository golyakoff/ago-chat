using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ago.Chat.Api.Channels;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Email;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
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
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-09`: the production `EmailWebhookEndpoints.MapEmailWebhookEndpoints` mapping - a real Kestrel host
/// on a real ephemeral loopback port, against a real Postgres (<see cref="PostgresFixture"/>) and the real
/// production <see cref="ReceiveChannelMessageHandler"/> chain, the identical technique
/// <see cref="WhatsAppWebhookEndpointsTests"/> already establishes for a channel's own webhook route.
///
/// <para><b>What this item's own report needs proven, that no channel before it had to prove:</b> tenant
/// routing with <em>no</em> <see cref="ChannelCredential"/> row at all - a site is mailable the instant it
/// exists, from the recipient address alone (<see cref="EmailRecipientAddress"/>'s own remarks) - and
/// <see cref="EmailThreadState"/> being written on the very first inbound message and updated (not
/// re-created) on the second.</para>
///
/// <para>What this does <b>not</b> prove: that a real inbound email, delivered through a real self-hosted
/// mail server, actually reaches this route as this exact JSON shape - <see cref="EmailInboundWebhookPayload"/>'s
/// own honesty note explains that the script which would produce that JSON from a real SMTP delivery does
/// not exist in this environment (ago-deploy's own work, out of this item's scope).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmailWebhookEndpointsTests(PostgresFixture fixture)
{
    private const string WebhookSecret = "test-webhook-secret-not-a-real-secret";
    private const string Domain = "ago-chat.example";

    [Fact]
    public async Task Delivery_WithACorrectSignatureAndAKnownSite_ReturnsOk_AndTheMessageReachesPostgresThroughTheRealHandlerChain()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = InboundMessageJson(siteId, from: "visitor@example.com", text: "Where is my order?", messageId: "<msg-1@visitor.example>");
        using var request = SignedRequest(body);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var storedBody = await ReadMessageBodyAsync(siteId);
        Assert.Equal("Where is my order?", storedBody);
    }

    [Fact]
    public async Task Delivery_WritesAnEmailThreadStateRowFromTheFirstInboundMessage()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = InboundMessageJson(
            siteId, from: "visitor@example.com", text: "Where is my order?", messageId: "<msg-1@visitor.example>",
            subject: "Order question");
        using var request = SignedRequest(body);

        await client.SendAsync(request);

        var thread = await ReadEmailThreadAsync(siteId);
        Assert.NotNull(thread);
        Assert.Equal("<msg-1@visitor.example>", thread!.Value.RootMessageId);
        Assert.Equal("<msg-1@visitor.example>", thread.Value.LastInboundMessageId);
        Assert.Equal("Order question", thread.Value.Subject);
    }

    /// <summary>A second inbound message in the same conversation must move
    /// <see cref="EmailThreadState.LastInboundMessageId"/> forward without touching
    /// <see cref="EmailThreadState.RootMessageId"/> - proven through the real route, not only at the
    /// domain-type level (<see cref="EmailThreadStateTests"/>'s own scope).</summary>
    [Fact]
    public async Task Delivery_ForASecondMessageInTheSameConversation_UpdatesLastInboundMessageId_KeepsRootMessageId()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        using var first = SignedRequest(InboundMessageJson(siteId, "visitor@example.com", "First message", "<msg-1@visitor.example>"));
        await client.SendAsync(first);
        using var second = SignedRequest(InboundMessageJson(siteId, "visitor@example.com", "Second message", "<msg-2@visitor.example>"));
        await client.SendAsync(second);

        var thread = await ReadEmailThreadAsync(siteId);
        Assert.NotNull(thread);
        Assert.Equal("<msg-1@visitor.example>", thread!.Value.RootMessageId);
        Assert.Equal("<msg-2@visitor.example>", thread.Value.LastInboundMessageId);
    }

    [Fact]
    public async Task Delivery_WithAnIncorrectSignature_ReturnsUnauthorized_AndWritesNothing()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = InboundMessageJson(siteId, "visitor@example.com", "an attacker's own forged delivery", "<msg-x@visitor.example>");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/email") { Content = content };
        request.Headers.Add(EmailWebhookEndpoints.SignatureHeaderName, "sha256=0000000000000000000000000000000000000000000000000000000000000000");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    [Fact]
    public async Task Delivery_WhenNoWebhookSecretIsConfigured_ReturnsUnauthorized()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildHostAsync(configureEmail: false);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = InboundMessageJson(siteId, "visitor@example.com", "hello", "<msg-1@visitor.example>");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/email") { Content = content };
        request.Headers.Add(EmailWebhookEndpoints.SignatureHeaderName, "sha256=irrelevant");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The central routing claim this channel makes that no channel before it could: a
    /// well-formed, correctly-signed delivery for a <see cref="SiteId"/> this deployment does not have is
    /// acknowledged, not rejected - <see cref="EmailRecipientAddress"/>'s own remarks on why a parseable id
    /// is not proof a site exists.</summary>
    [Fact]
    public async Task Delivery_ForAWellFormedButUnknownSiteId_ReturnsOk_ButWritesNothing()
    {
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var unknownSiteId = new SiteId(Guid.NewGuid());
        var body = InboundMessageJson(unknownSiteId, "visitor@example.com", "nobody owns this site", "<msg-1@visitor.example>");
        using var request = SignedRequest(body);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ReadMessageBodyAsync(unknownSiteId));
    }

    [Fact]
    public async Task Delivery_ForARecipientThatDoesNotMatchThisDeploymentsOwnSubaddressShape_ReturnsOk_ButWritesNothing()
    {
        var siteId = await SeedSiteAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = JsonSerializer.Serialize(new
        {
            from = "visitor@example.com",
            to = "postmaster@ago-chat.example",
            subject = "bounce",
            text = "not routable",
            messageId = "<msg-1@visitor.example>",
        });
        using var request = SignedRequest(body);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    [Fact]
    public async Task Delivery_WithMalformedJson_ReturnsBadRequest()
    {
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        using var request = SignedRequest("{ this is not valid json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string InboundMessageJson(
        SiteId siteId, string from, string text, string messageId, string subject = "Support request") =>
        JsonSerializer.Serialize(new
        {
            from,
            to = $"support+{siteId.Value:N}@{Domain}",
            subject,
            text,
            messageId,
        });

    private static HttpRequestMessage SignedRequest(string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), bodyBytes));

        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/email") { Content = content };
        request.Headers.Add(EmailWebhookEndpoints.SignatureHeaderName, $"sha256={signature}");
        return request;
    }

    private async Task<string?> ReadMessageBodyAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT m.body FROM messages m JOIN conversations c ON c.id = m.conversation_id WHERE c.site_id = @siteId", connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<(string RootMessageId, string LastInboundMessageId, string Subject)?> ReadEmailThreadAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT t.root_message_id, t.last_inbound_message_id, t.subject
            FROM email_threads t
            JOIN conversations c ON c.id = t.conversation_id
            WHERE c.site_id = @siteId
            """, connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync(CancellationToken.None);

        return siteId;
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private async Task<TestHost> BuildHostAsync(bool configureEmail = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<IEmailThreadStore, EmailThreadStore>();
        builder.Services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
        builder.Services.AddScoped<IVisitorRepository, VisitorRepository>();
        builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
        builder.Services.AddScoped<IPendingChannelLinkRequestRepository, PendingChannelLinkRequestRepository>();
        builder.Services.AddScoped<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton(new MessageSendRateLimitOptions());
        builder.Services.AddSingleton<IMessagePipeline>(_ => new SynchronousMessagePipeline(fixture.DataSource));
        builder.Services.AddScoped<StartConversationHandler>();
        builder.Services.AddScoped<SendVisitorMessageHandler>();
        builder.Services.AddScoped<ReceiveChannelMessageHandler>();
        builder.Services.AddSingleton(Options.Create(configureEmail
            ? new EmailBotApiOptions { Domain = Domain, WebhookSecret = WebhookSecret }
            : new EmailBotApiOptions { Domain = Domain }));

        var app = builder.Build();

        // The real production mapping - no duplicated route or handler logic, the same discipline
        // WhatsAppWebhookEndpointsTests' own BuildHostAsync states for itself.
        app.MapEmailWebhookEndpoints();

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }
}
