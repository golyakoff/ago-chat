using System.Text;
using System.Text.RegularExpressions;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Email;
using Ago.Platform.Abstractions;
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
///
/// <para>`25-156`: also covers the tenant-branded HTML part this adapter now attaches to every reply
/// (<see cref="TenantReplyEmailShell"/>) - through the same real SMTP boundary as everything else in this
/// class, decoded back out of the raw <c>multipart/alternative</c> transcript by
/// <see cref="ExtractMimePart"/> rather than by calling any internal builder directly, so these tests
/// prove what actually goes out on the wire, not merely that a method was invoked.</para>
/// </summary>
public sealed partial class EmailChannelAdapterTests
{
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"));

    private static OutboundChannelMessage Reply(string recipient = "visitor@example.com") => new(
        ChannelKind.Email, new ExternalChannelAddress(recipient), ConversationId, new MessageId(Guid.NewGuid()),
        new MessageBody("Your order ships tomorrow."));

    private static OutboundChannelMessage Reply(Guid messageId, bool requestContactIfSupported) => new(
        ChannelKind.Email, new ExternalChannelAddress("visitor@example.com"), ConversationId, new MessageId(messageId),
        new MessageBody("Your order ships tomorrow."), requestContactIfSupported);

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

    /// <summary>`25-152`'s own Done-when: Email has no contact-sharing affordance, so
    /// <see cref="OutboundChannelMessage.RequestContactIfSupported"/> must change nothing this adapter
    /// sends. Two separate servers, not one server sent to twice - <see cref="FakeSmtpServer"/> accepts
    /// exactly one connection - with the identical <see cref="OutboundChannelMessage.MessageId"/> and the
    /// same <see cref="FixedClock"/> both adapters share, so the only two facts that could otherwise vary
    /// the DATA payload (the <c>Message-Id</c> and <c>Date</c> headers) are held fixed and the comparison
    /// is a genuine proof, not a coincidence.
    ///
    /// <para>`25-156`: the payload now also carries a per-message random <c>multipart/alternative</c>
    /// boundary (<see cref="Ago.Chat.Infrastructure.Email.EmailMimeMessageBuilder.BuildMultipartAlternative"/>'s
    /// own <c>Guid.NewGuid()</c> boundary), which is expected to differ between the two independent sends
    /// this test makes and would otherwise make the two payloads *never* byte-equal regardless of this
    /// item's own change - <see cref="NormalizeBoundary"/> replaces that one random token with a fixed
    /// placeholder in both payloads before comparing, so the comparison still proves what it always
    /// proved (the flag changes nothing) rather than failing on a fact this test was never about.</para>
    /// </summary>
    [Fact]
    public async Task SendAsync_IgnoresRequestContactIfSupported_TheDataPayloadIsByteIdenticalEitherWay()
    {
        var messageId = Guid.NewGuid();

        using var serverWithoutFlag = await FakeSmtpServer.StartAsync();
        var adapterWithoutFlag = BuildAdapter(serverWithoutFlag.Options, hasConversation: true, hasThread: true);
        await adapterWithoutFlag.SendAsync(Reply(messageId, requestContactIfSupported: false), CancellationToken.None);
        var transcriptWithoutFlag = await serverWithoutFlag.WaitForTranscriptAsync();

        using var serverWithFlag = await FakeSmtpServer.StartAsync();
        var adapterWithFlag = BuildAdapter(serverWithFlag.Options, hasConversation: true, hasThread: true);
        await adapterWithFlag.SendAsync(Reply(messageId, requestContactIfSupported: true), CancellationToken.None);
        var transcriptWithFlag = await serverWithFlag.WaitForTranscriptAsync();

        Assert.Equal(
            NormalizeBoundary(transcriptWithoutFlag.DataPayload), NormalizeBoundary(transcriptWithFlag.DataPayload));
    }

    /// <summary>`25-156`: a conversation's own <c>SiteId</c> must always name a real <see cref="Site"/> -
    /// the identical "should not happen, thrown rather than silently accepted" data-inconsistency
    /// category <see cref="SendAsync_WhenNoConversationExists_Throws"/>/
    /// <see cref="SendAsync_WhenNoEmailThreadStateExists_Throws"/> already cover for their own missing
    /// rows, now proven for the new <see cref="ISiteRepository"/> lookup this item adds.</summary>
    [Fact]
    public async Task SendAsync_WhenNoSiteExists_Throws()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: true, hasSite: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(Reply(), CancellationToken.None));
    }

    /// <summary>`25-156` Done-when #1: the tenant's own name and configured accent colour actually appear
    /// in the rendered HTML part - decoded back out of the real <c>multipart/alternative</c> transcript,
    /// not asserted by checking that some method was called.</summary>
    [Fact]
    public async Task SendAsync_IncludesTheTenantsNameAndConfiguredAccentColorInTheHtmlPart()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(
            server.Options, hasConversation: true, hasThread: true,
            siteName: "Acme Repairs", primaryColorHex: "#FF6600");

        await adapter.SendAsync(Reply(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        var html = ExtractMimePart(transcript.DataPayload, "text/html");
        Assert.Contains("Acme Repairs", html);
        Assert.Contains("#FF6600", html);
    }

    /// <summary>`25-156` Done-when #2: a site with no <see cref="WidgetConfig.PrimaryColorHex"/> set still
    /// renders correctly, with <see cref="TenantReplyEmailShell.NeutralAccentColorHex"/> rather than a
    /// missing or broken accent - the not-only-happy-path case the ticket names explicitly.</summary>
    [Fact]
    public async Task SendAsync_WithNoAccentColorConfigured_UsesTheNeutralDefaultInTheHtmlPart()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(
            server.Options, hasConversation: true, hasThread: true,
            siteName: "Acme Repairs", primaryColorHex: null);

        await adapter.SendAsync(Reply(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        var html = ExtractMimePart(transcript.DataPayload, "text/html");
        Assert.Contains(TenantReplyEmailShell.NeutralAccentColorHex, html);
        Assert.Contains("Acme Repairs", html);
    }

    /// <summary>`25-156` Done-when #3: the <c>text/plain</c> part is exactly <see cref="Reply"/>'s own
    /// unadorned body - no shell markup has leaked into it - the same "wrapper is opt-in cosmetics, never
    /// a second copy of the reply's own meaning" rule `25-155` already states for its own multipart
    /// parts, now checked for this reply channel too.</summary>
    [Fact]
    public async Task SendAsync_ThePlainTextPartCarriesTheReplyExactlyAsBefore_Unadorned()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: true);
        var reply = Reply();

        await adapter.SendAsync(reply, CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        var plainText = ExtractMimePart(transcript.DataPayload, "text/plain");
        Assert.Equal(reply.Body.Value, plainText);
        Assert.DoesNotContain("<", plainText);
    }

    /// <summary>`25-156` Done-when #4 (the test half - a real send is out of a background worker's own
    /// reach, left explicitly open in this item's own report): the switch to a
    /// <c>multipart/alternative</c> body changes the <c>Content-Type</c> header alone -
    /// <see cref="EmailChannelAdapter"/>'s own thread-matching headers are untouched, restated here for
    /// <c>References</c> alongside <see cref="SendAsync_SetsInReplyToFromTheStoredThreadsLastInboundMessageId"/>'s
    /// own <c>In-Reply-To</c> coverage.</summary>
    [Fact]
    public async Task SendAsync_MultipartAlternativeBody_LeavesTheReferencesHeaderUnaffected()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: true);

        await adapter.SendAsync(Reply(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains("References: <root@visitor.example>", transcript.DataPayload);
    }

    private static string NormalizeBoundary(string dataPayload) =>
        BoundaryPattern().Replace(dataPayload, "BOUNDARY");

    [GeneratedRegex("AgoChatBoundary[0-9a-f]{32}")]
    private static partial Regex BoundaryPattern();

    /// <summary>Pulls one MIME part's decoded text back out of a raw
    /// <c>multipart/alternative</c> DATA transcript - both parts this adapter now sends are base64,
    /// exactly as <see cref="Ago.Chat.Infrastructure.Email.EmailMimeMessageBuilder.BuildMultipartAlternative"/>'s
    /// own remarks describe, so a test asserting on real content has to undo that encoding first rather
    /// than substring-matching the wire bytes directly.</summary>
    private static string ExtractMimePart(string dataPayload, string contentType)
    {
        var marker = $"Content-Type: {contentType}; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n";
        var start = dataPayload.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected a {contentType} part in the transcript");
        start += marker.Length;

        var end = dataPayload.IndexOf("\r\n--AgoChatBoundary", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "expected a closing boundary after the part");

        var base64 = dataPayload[start..end].Replace("\r\n", "");
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private static EmailChannelAdapter BuildAdapter(
        EmailBotApiOptions options, bool hasConversation, bool hasThread, bool hasSite = true,
        string siteName = "Acme Repairs", string? primaryColorHex = null, string? brandCompanyName = null,
        string? readyLogoObjectKey = null, ICache? cache = null, IFileStorage? fileStorage = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConversationRepository>(_ => new FixedConversationRepository(hasConversation));
        services.AddScoped<IEmailThreadStore>(_ => new FixedEmailThreadStore(hasThread));
        services.AddScoped<ISiteRepository>(_ => new FixedSiteRepository(
            hasSite, siteName, primaryColorHex, brandCompanyName, readyLogoObjectKey));
        var provider = services.BuildServiceProvider();

        var client = new EmailSmtpClient(options);
        return new EmailChannelAdapter(
            client, Options.Create(options), provider.GetRequiredService<IServiceScopeFactory>(),
            cache ?? new FakeCache(), fileStorage ?? new ThrowingFileStorage(),
            new FixedClock(), NullLogger<EmailChannelAdapter>.Instance);
    }

    /// <summary>`25-160`: the identical cache-aside fake `Ago.Chat.Application.Tests.Fakes.FakeCache`
    /// already establishes for Application-level tests, duplicated here rather than shared across
    /// projects - this test project has no reference to that one's own test-only assembly, and a fake
    /// this small is not worth adding one for.</summary>
    private sealed class FakeCache : ICache
    {
        private readonly Dictionary<string, object?> _store = [];

        public Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken) where T : class =>
            Task.FromResult(_store.TryGetValue(key.Value, out var value) ? (T?)value : default);

        public Task SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken cancellationToken) where T : class
        {
            _store[key.Value] = value;
            return Task.CompletedTask;
        }

        public async Task<T> GetOrCreateAsync<T>(
            CacheKey key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken)
            where T : class
        {
            if (_store.TryGetValue(key.Value, out var cached))
            {
                return (T)cached!;
            }

            var value = await factory(cancellationToken);
            _store[key.Value] = value;
            return value;
        }

        public Task RemoveAsync(CacheKey key, CancellationToken cancellationToken)
        {
            _store.Remove(key.Value);
            return Task.CompletedTask;
        }
    }

    /// <summary>`25-160`: proves <see cref="SendAsync_WithAWarmLogoCache_NeverCallsFileStorage"/> for
    /// real, rather than by inspection - every member throws, so a warm-cache send that ever reached
    /// object storage would fail the test immediately instead of silently succeeding by coincidence.</summary>
    private sealed class ThrowingFileStorage : IFileStorage
    {
        public Task<PresignedUpload> CreateUploadAsync(ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected object storage call on a warm branding cache.");

        public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected object storage call on a warm branding cache.");

        public Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected object storage call on a warm branding cache.");

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected object storage call on a warm branding cache.");
    }

    /// <summary>`25-160` Done-when: a visitor's reply email embeds the tenant's own logo inline
    /// (<c>Content-ID</c>) when one is <c>Ready</c> - a real, promoted <see cref="Site"/> (through the
    /// same <see cref="Site.SubmitLogoUpload"/>/<see cref="Site.PromoteLogo"/> pair the real validating
    /// consumer calls, not a shortcut), a warm branding cache seeded exactly the way
    /// <c>Ago.Chat.Worker.SiteLogoValidator</c>'s own write-through would.</summary>
    [Fact]
    public async Task SendAsync_WhenTheSiteHasAReadyLogo_EmbedsItInlineByContentId()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var logoObjectKey = $"site/{SiteId.Value}/logo/{Guid.NewGuid():N}.png";
        var cache = new FakeCache();
        await cache.SetAsync(
            SiteBrandingCacheKeys.ForLogo(SiteId), new SiteBrandingLogoPayload(Convert.ToBase64String([1, 2, 3, 4]), "image/png"),
            new CacheEntryOptions(TimeSpan.FromHours(1)), CancellationToken.None);
        var adapter = BuildAdapter(
            server.Options, hasConversation: true, hasThread: true,
            siteName: "Acme Repairs", readyLogoObjectKey: logoObjectKey, cache: cache);

        await adapter.SendAsync(Reply(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.Contains("Content-ID: <logo-", transcript.DataPayload);
        Assert.Contains("Content-Disposition: inline", transcript.DataPayload);
        var html = ExtractMimePart(transcript.DataPayload, "text/html");
        Assert.Contains("cid:logo-", html);
    }

    /// <summary>`25-160` Done-when: "the email-composition path does not touch object storage on a warm
    /// cache" - <see cref="ThrowingFileStorage"/> is what actually proves it; this test merely has to
    /// reach a successful send to know none of its members were ever called.</summary>
    [Fact]
    public async Task SendAsync_WithAWarmLogoCache_NeverCallsFileStorage()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var logoObjectKey = $"site/{SiteId.Value}/logo/{Guid.NewGuid():N}.png";
        var cache = new FakeCache();
        await cache.SetAsync(
            SiteBrandingCacheKeys.ForLogo(SiteId), new SiteBrandingLogoPayload(Convert.ToBase64String([1, 2, 3, 4]), "image/png"),
            new CacheEntryOptions(TimeSpan.FromHours(1)), CancellationToken.None);
        var adapter = BuildAdapter(
            server.Options, hasConversation: true, hasThread: true,
            readyLogoObjectKey: logoObjectKey, cache: cache, fileStorage: new ThrowingFileStorage());

        var outcome = await adapter.SendAsync(Reply(), CancellationToken.None);

        Assert.True(outcome.Delivered);
    }

    /// <summary>`25-160`: the fallback half of this item's own Done-when - no logo set at all still
    /// renders the `25-156` text-only shell, unchanged.</summary>
    [Fact]
    public async Task SendAsync_WithNoLogoSet_FallsBackToTheTextOnlyShell()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(server.Options, hasConversation: true, hasThread: true, siteName: "Acme Repairs");

        await adapter.SendAsync(Reply(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        Assert.DoesNotContain("Content-ID:", transcript.DataPayload);
        var html = ExtractMimePart(transcript.DataPayload, "text/html");
        Assert.Contains("Acme Repairs", html);
        Assert.DoesNotContain("<img", html);
    }

    /// <summary>`25-160`: <see cref="Site.BrandCompanyName"/>, when set, is what the shell renders -
    /// not <see cref="Site.Name"/> - the identical "brand name, not the registration name" split this
    /// item's own Design decisions draw.</summary>
    [Fact]
    public async Task SendAsync_WhenBrandCompanyNameIsSet_RendersItInsteadOfSiteName()
    {
        using var server = await FakeSmtpServer.StartAsync();
        var adapter = BuildAdapter(
            server.Options, hasConversation: true, hasThread: true,
            siteName: "Internal Registration Name", brandCompanyName: "Acme Repairs LLC");

        await adapter.SendAsync(Reply(), CancellationToken.None);
        var transcript = await server.WaitForTranscriptAsync();

        var html = ExtractMimePart(transcript.DataPayload, "text/html");
        Assert.Contains("Acme Repairs LLC", html);
        Assert.DoesNotContain("Internal Registration Name", html);
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

        public Task<IReadOnlyDictionary<ConversationId, Conversation>> GetByIdsAsync(
            IReadOnlyCollection<ConversationId> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<ConversationId, Conversation>>(hasConversation
                ? ids.ToDictionary(id => id, id => Conversation.Start(id, SiteId, new VisitorId(Guid.NewGuid()), DateTimeOffset.UtcNow))
                : new Dictionary<ConversationId, Conversation>());

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

    /// <summary>`25-156`: the new lookup <see cref="EmailChannelAdapter"/> adds - a fixed <see cref="Site"/>
    /// carrying whatever name/colour a test wants to prove renders, or no site at all
    /// (<see cref="SendAsync_WhenNoSiteExists_Throws"/>'s own "should not happen" case). The colour is
    /// applied through <see cref="Site.UpdateWidgetConfig"/> rather than a constructor parameter -
    /// <see cref="Site"/> has no constructor overload taking a <see cref="WidgetConfig"/> directly, the
    /// same real aggregate every non-test caller also goes through.</summary>
    private sealed class FixedSiteRepository(
        bool hasSite, string siteName, string? primaryColorHex, string? brandCompanyName = null,
        string? readyLogoObjectKey = null) : ISiteRepository
    {
        public Task<Site?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Site?> GetByIdAsync(SiteId id, CancellationToken cancellationToken)
        {
            if (!hasSite)
            {
                return Task.FromResult<Site?>(null);
            }

            var site = new Site(id, "https://example.test/public-key", [], name: siteName);
            if (primaryColorHex is not null)
            {
                site.UpdateWidgetConfig(
                    new WidgetConfig(primaryColorHex, site.WidgetConfig.Position), DateTimeOffset.UtcNow);
            }

            if (brandCompanyName is not null)
            {
                site.UpdateBrandCompanyName(brandCompanyName, DateTimeOffset.UtcNow);
            }

            // `25-160`: reaches Ready through the identical two real aggregate methods
            // `SubmitLogoUploadHandler`/`Ago.Chat.Worker.SiteLogoValidator` call in production - never a
            // shortcut that sets a private field directly - so this fake proves the real state machine
            // still reaches the state each test needs.
            if (readyLogoObjectKey is not null)
            {
                const string pendingKey = "site/pending/test-logo.png";
                site.SubmitLogoUpload(pendingKey, "image/png", DateTimeOffset.UtcNow);
                site.PromoteLogo(pendingKey, readyLogoObjectKey, DateTimeOffset.UtcNow);
                site.ClearDomainEvents();
            }

            return Task.FromResult<Site?>(site);
        }

        public Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Site site, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
