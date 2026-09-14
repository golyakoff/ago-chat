using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.GetAttachmentDownloadUrl;

public class GetAttachmentDownloadUrlHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetAttachmentDownloadUrlHandler Handler,
        FakeFileStorage FileStorage,
        Attachment Attachment,
        Conversation Conversation,
        FakePermissionChecker Permissions,
        FakeAttachmentEgressMeter EgressMeter,
        FakeAttachmentEgressReadStore EgressReads,
        FakeDownloadThresholdReadStore Thresholds,
        FakeSiteRepository Sites,
        Site Site,
        FakeAttachmentRepository Attachments,
        FakeConversationRepository Conversations);

    /// <summary>`25-83`: unbounded by default (`long.MaxValue`/`long.MaxValue`) - the identical
    /// "missing configuration fails open" contract <see cref="FakeDownloadThresholdReadStore"/>'s own
    /// remarks describe, so every test above this one (written before the hard-block gate existed)
    /// keeps passing unchanged: nobody who does not explicitly <c>Thresholds.Seed(...)</c> a low
    /// threshold below can ever have a download refused.</summary>
    private static Fixture CreateFixture(AttachmentState state = AttachmentState.Ready, bool assignOperator = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        if (assignOperator)
        {
            conversation.AssignTo(OperatorId, Now);
        }

        conversations.Seed(conversation);

        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), SiteId, conversation.Id, "site/x/conv/y/z.png", "image/png", 42, Now);
        if (state != AttachmentState.Pending)
        {
            attachment.ConfirmReady(42, "image/png", Now);
        }

        if (state == AttachmentState.Deleted)
        {
            attachment.MarkDeleted();
        }

        var attachments = new FakeAttachmentRepository();
        attachments.Seed(attachment);

        var site = new Site(SiteId, "pk-" + SiteId.Value, allowedOrigins: []);
        var sites = new FakeSiteRepository();
        sites.Seed(site);

        var fileStorage = new FakeFileStorage();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        var egressMeter = new FakeAttachmentEgressMeter();
        var egressReads = new FakeAttachmentEgressReadStore();
        var thresholds = new FakeDownloadThresholdReadStore();
        var handler = new GetAttachmentDownloadUrlHandler(
            attachments,
            conversations,
            sites,
            fileStorage,
            permissions,
            new FakeCache(),
            egressMeter,
            egressReads,
            thresholds,
            new AttachmentOptions(),
            new FakeClock(Now),
            new FakeIdGenerator(),
            NullLogger<GetAttachmentDownloadUrlHandler>.Instance);

        return new Fixture(
            handler, fileStorage, attachment, conversation, permissions, egressMeter, egressReads, thresholds, sites, site,
            attachments, conversations);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenAParticipantAndReady_ReturnsAUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(fixture.Attachment.ObjectKey, result.Value.Url.ToString());
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenNotAParticipant_ReturnsForbidden()
    {
        var fixture = CreateFixture();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, someoneElse), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenNotAssignedToTheConversation_ReturnsForbidden()
    {
        var fixture = CreateFixture(assignOperator: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new GetAttachmentDownloadUrlAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheAttachmentIsStillPending_ReturnsNotReady()
    {
        var fixture = CreateFixture(state: AttachmentState.Pending);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotReady", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenNoThumbnailWasEverGenerated_ReturnsANullThumbnailUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ThumbnailUrl);
        Assert.Equal("image/png", result.Value.ContentType);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenAThumbnailExists_ReturnsAPresignedThumbnailUrl()
    {
        var fixture = CreateFixture();
        fixture.Attachment.SetThumbnail("site/x/conv/y/z-thumb.webp");

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.ThumbnailUrl);
        Assert.Contains("z-thumb.webp", result.Value.ThumbnailUrl!.ToString());
    }

    [Fact]
    public async Task HandleAsVisitorAsync_CalledTwice_OnlyPresignsOnce_TheSecondCallIsServedFromCache()
    {
        var fixture = CreateFixture();

        var first = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);
        var second = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value.Url, second.Value.Url);
        Assert.Equal(1, fixture.FileStorage.CreateDownloadUrlCalls);
    }

    /// <summary>`23-80`: a deleted attachment must read as gone, permanently - distinct from
    /// `Attachment.NotReady` (transient, worth retrying). See ConversationErrors.AttachmentRemoved's
    /// own remarks for why the two codes must not collapse into one.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheAttachmentWasDeleted_ReturnsRemoved_NotNotReady()
    {
        var fixture = CreateFixture(state: AttachmentState.Deleted);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.Removed", result.Error!.Value.Code);
    }

    /// <summary>`23-82`/`23-80`: the one place a download is counted - "at the point a presigned GET
    /// is issued." A fresh presign (a cache miss) must bump the attachment's own counter and the
    /// site's monthly egress aggregate together.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_OnAFreshPresign_RecordsTheDownloadAndTheSiteEgress()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);

        var saved = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(1, saved!.DownloadCount);
        Assert.Equal(Now, saved.LastDownloadedAt);

        var recorded = Assert.Single(fixture.EgressMeter.Records);
        Assert.Equal(SiteId, recorded.SiteId);
        Assert.Equal(new DateOnly(Now.Year, Now.Month, 1), recorded.PeriodMonth);
        Assert.Equal(fixture.Attachment.SizeBytes, recorded.Bytes);
    }

    /// <summary>The companion to the fresh-presign test above - a cache hit must not double-count,
    /// because `RecordDownloadAsync` only ever runs inside the cache factory.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_CalledTwiceWithinTheCacheWindow_RecordsExactlyOneDownload()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);
        await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        var saved = await fixture.Attachments.GetByIdAsync(fixture.Attachment.Id, CancellationToken.None);
        Assert.Equal(1, saved!.DownloadCount);
        Assert.Single(fixture.EgressMeter.Records);
    }

    // `25-83`: the hard download-block threshold - "every presigned GET refuses, for everyone -
    // operator and visitor alike, no carve-out" (docs/backlog/25-83-*.md's own decision).

    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheSiteIsAtItsHardThreshold_ReturnsDownloadBlocked()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: 100, hardThresholdBytes: 200);
        fixture.EgressReads.SeedBytesOut(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 200);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.DownloadBlocked", result.Error!.Value.Code);
    }

    /// <summary>The operator path gets no carve-out either - the deliberate choice this item's own
    /// class remarks state against `23-82`'s own "refusing a download is worse than refusing an
    /// upload" instinct.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheSiteIsAtItsHardThreshold_ReturnsDownloadBlocked()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: 100, hardThresholdBytes: 200);
        fixture.EgressReads.SeedBytesOut(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 200);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new GetAttachmentDownloadUrlAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.DownloadBlocked", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_WhenBelowTheHardThreshold_StillSucceeds()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: 100, hardThresholdBytes: 200);
        fixture.EgressReads.SeedBytesOut(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 199);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    /// <summary>The owner's own override, proven in both directions: blocked before the exemption is
    /// granted, genuinely bypassed once it is - `docs/backlog/25-83-*.md`'s own explicit demand
    /// ("proven to actually bypass the hard block once granted").</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheSiteIsExempt_BypassesTheHardBlock()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: 100, hardThresholdBytes: 200);
        fixture.EgressReads.SeedBytesOut(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 500);

        var blockedBefore = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);
        Assert.True(blockedBefore.IsFailure);
        Assert.Equal("Attachment.DownloadBlocked", blockedBefore.Error!.Value.Code);

        fixture.Site.GrantDownloadBlockExemption("owner@example.com", "goodwill exception", Now);
        await fixture.Sites.SaveAsync(fixture.Site, CancellationToken.None);

        var allowedAfter = await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);
        Assert.True(allowedAfter.IsSuccess);
    }

    /// <summary>"A visitor's refused download drops a locale-aware system message into that exact
    /// conversation" - `docs/backlog/25-83-*.md`'s own Done-when, proven against the English default
    /// (the Russian half is `DownloadBlockedTextTests`, unit-testing the pure text function directly
    /// rather than round-tripping a second full handler test through a second seeded `Locale.Ru`
    /// site).</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenBlocked_AddsASystemMessageToTheConversation()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: 100, hardThresholdBytes: 200);
        fixture.EgressReads.SeedBytesOut(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 200);

        await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        var saved = await fixture.Conversations.GetByIdAsync(fixture.Conversation.Id, CancellationToken.None);
        var systemMessage = Assert.Single(saved!.Messages, m => m.AuthorKind == MessageAuthorKind.System);
        Assert.Contains("monthly download limit", systemMessage.Body.Value);
    }

    /// <summary>Locale resolution - the same round-trip-through-the-handler shape
    /// `RouteConversationToModuleHandlerTests` already uses for its own four sibling texts
    /// (`site.UpdateLocale(Locale.Ru, Now)`, then assert the Russian text landed), reused here rather
    /// than reaching for reflection against a private static method.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_WhenTheSiteIsRussian_AddsTheRussianSystemMessage()
    {
        var fixture = CreateFixture();
        fixture.Site.UpdateLocale(Locale.Ru, Now);
        fixture.Thresholds.Seed("free", softThresholdBytes: 100, hardThresholdBytes: 200);
        fixture.EgressReads.SeedBytesOut(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 200);

        await fixture.Handler.HandleAsVisitorAsync(
            new GetAttachmentDownloadUrlAsVisitor(fixture.Attachment.Id, VisitorId), CancellationToken.None);

        var saved = await fixture.Conversations.GetByIdAsync(fixture.Conversation.Id, CancellationToken.None);
        var systemMessage = Assert.Single(saved!.Messages, m => m.AuthorKind == MessageAuthorKind.System);
        Assert.Contains("месячного лимита скачиваний", systemMessage.Body.Value);
    }

    /// <summary>The operator path's own mirror - no system message, ever: `docs/backlog/25-83-*.md`'s
    /// own decision names only a visitor's refused download, and an operator already sees the refusal
    /// directly in their own console, unlike a visitor who has no console to read it from.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_WhenBlocked_AddsNoSystemMessage()
    {
        var fixture = CreateFixture();
        fixture.Thresholds.Seed("free", softThresholdBytes: 100, hardThresholdBytes: 200);
        fixture.EgressReads.SeedBytesOut(SiteId, new DateOnly(Now.Year, Now.Month, 1), bytesOut: 200);

        await fixture.Handler.HandleAsOperatorAsync(
            new GetAttachmentDownloadUrlAsOperator(fixture.Attachment.Id, OperatorId, SiteId), CancellationToken.None);

        var saved = await fixture.Conversations.GetByIdAsync(fixture.Conversation.Id, CancellationToken.None);
        Assert.Empty(saved!.Messages);
    }
}
