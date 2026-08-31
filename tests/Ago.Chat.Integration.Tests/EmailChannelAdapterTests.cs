using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Email;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-09`: <see cref="EmailChannelAdapter.SendAsync"/>'s own routing/threading-header logic - the parts
/// specific to this adapter rather than the generic resilience wrapping around it
/// (<see cref="ResilientInboundChannelAdapterTests"/>'s own scope) or the raw SMTP protocol/MIME shape
/// (<see cref="EmailSmtpClientTests"/>'s own scope, which this class reuses <see cref="FakeSmtpServer"/>
/// from). Uses the identical minimal fake-repository technique <see cref="WhatsAppChannelAdapterTests"/>
/// already establishes.
/// </summary>
public sealed class EmailChannelAdapterTests
{
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"));

    private static OutboundChannelMessage Reply(string recipient = "visitor@example.com") => new(
        ChannelKind.Email, new ExternalChannelAddress(recipient), ConversationId, new MessageId(Guid.NewGuid()),
        new MessageBody("Your order ships tomorrow."));

    [Fact]
    public async Task SendAsync_WhenTheRelayAccepts_ReturnsSentWithTheProviderMessageId()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: true);

        var outcome = await adapter.SendAsync(Reply(), CancellationToken.None);

        Assert.True(outcome.Delivered);
    }

    /// <summary>Proves the From address is built from the conversation's own SiteId, not a fixed or
    /// caller-supplied value - EmailRecipientAddress's own subaddress scheme,
    /// <see cref="EmailChannelAdapter"/>'s own remarks on why the conversation must be loaded to reach
    /// it.</summary>
    [Fact]
    public async Task SendAsync_UsesTheSitesOwnSubaddressedSupportAddressAsTheFromAddress()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: true);

        await adapter.SendAsync(Reply(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains($"MAIL FROM:<support+{SiteId.Value:N}@ago-chat.example>", transcript.Commands);
    }

    /// <summary>Threading headers come from <see cref="IEmailThreadStore"/>, not invented per send - the
    /// whole point of `14-09`'s own "carrying enough threading headers" scope.</summary>
    [Fact]
    public async Task SendAsync_SetsInReplyToFromTheStoredThreadsLastInboundMessageId()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: true);

        await adapter.SendAsync(Reply(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains("In-Reply-To: <root@visitor.example>", transcript.DataPayload);
    }

    [Fact]
    public async Task SendAsync_WhenNoConversationExists_Throws()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: false, hasThread: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(Reply(), CancellationToken.None));
    }

    /// <summary>The "should not happen" case <see cref="EmailChannelAdapter"/>'s own remarks describe - a
    /// conversation on the Email channel with no <see cref="EmailThreadState"/> row is a data
    /// inconsistency, thrown rather than surfaced as an ordinary refusal.</summary>
    [Fact]
    public async Task SendAsync_WhenNoEmailThreadStateExists_Throws()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(Reply(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_WhenTheRelayRefusesTheRecipient_ReturnsRefused()
    {
        using var server = await FakeSmtpServer.StartAsync(rcptToResponse: "550 5.1.1 No such user here");
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: true);

        var outcome = await adapter.SendAsync(Reply(), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("550", outcome.FailureReason);
    }

    private static EmailChannelAdapter BuildAdapter(EmailBotApiOptions options, bool hasConversation, bool hasThread)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConversationRepository>(_ => new FixedConversationRepository(hasConversation));
        services.AddScoped<IEmailThreadStore>(_ => new FixedEmailThreadStore(hasThread));
        var provider = services.BuildServiceProvider();

        var client = new EmailSmtpClient(options);
        return new EmailChannelAdapter(
            client, Options.Create(options), provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(), NullLogger<EmailChannelAdapter>.Instance);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedConversationRepository(bool hasConversation) : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult(hasConversation
                ? Conversation.Start(id, SiteId, new VisitorId(Guid.NewGuid()), DateTimeOffset.UtcNow)
                : null);

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedEmailThreadStore(bool hasThread) : IEmailThreadStore
    {
        public Task<EmailThreadState?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(hasThread
                ? EmailThreadState.Start(conversationId, "<root@visitor.example>", "Where is my order?")
                : null);

        public Task SaveAsync(EmailThreadState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
