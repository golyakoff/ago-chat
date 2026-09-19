using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SubmitLogoUpload;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.SubmitLogoUpload;

/// <summary>
/// `25-160`'s own Done-when: "an oversized file [and] a wrong format... are each rejected with a clear
/// reason - proven by explicit tests" and "more than 5 upload attempts for one site in a day are
/// refused by the rate limiter, proven by a test." The real, decode-based checks (dimensions, animated)
/// live entirely in `Ago.Chat.Worker.SiteLogoValidator` (`SiteLogoValidatorEndToEndTests`'s own scope) -
/// this handler's own job is exactly the cheap, decode-nothing checks and the rate limit, proven here.
/// </summary>
public class SubmitLogoUploadHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        SubmitLogoUploadHandler Handler, FakeSiteRepository Sites, FakeOutboxWriter Outbox, FakePresignedUrlUploader Uploader);

    private static Fixture CreateFixture(bool grantPermission = true, IRateLimiter? rateLimiter = null)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var outbox = new FakeOutboxWriter();
        var uploader = new FakePresignedUrlUploader();
        var handler = new SubmitLogoUploadHandler(
            sites, new FakeFileStorage(), uploader, rateLimiter ?? new FakeRateLimiter(), permissions, outbox,
            new SiteLogoOptions(), new LogoUploadRateLimitOptions(), new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, sites, outbox, uploader);
    }

    private static byte[] Bytes(int length) => new byte[length];

    [Fact]
    public async Task HandleAsync_WhenPermitted_UploadsAndRecordsThePendingStatus()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SubmitLogoUpload.SubmitLogoUpload(SiteId, OperatorId, "image/png", Bytes(1024)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(LogoStatus.Pending, result.Value.Status);
        Assert.Equal(1, fixture.Uploader.CallCount);
        Assert.Single(fixture.Outbox.Enqueued);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(LogoStatus.Pending, saved!.LogoStatus);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden_AndUploadsNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SubmitLogoUpload.SubmitLogoUpload(SiteId, OperatorId, "image/png", Bytes(1024)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(0, fixture.Uploader.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WithAnUnsupportedContentType_IsRejected_BeforeAnyUpload()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SubmitLogoUpload.SubmitLogoUpload(SiteId, OperatorId, "application/pdf", Bytes(1024)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.LogoInvalidContentType", result.Error!.Value.Code);
        Assert.Equal(0, fixture.Uploader.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WithAnOversizedFile_IsRejected_BeforeAnyUpload()
    {
        var fixture = CreateFixture();
        var options = new SiteLogoOptions();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SubmitLogoUpload.SubmitLogoUpload(
                SiteId, OperatorId, "image/png", Bytes((int)options.MaxSizeBytes + 1)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.LogoTooLarge", result.Error!.Value.Code);
        Assert.Equal(0, fixture.Uploader.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WhenRateLimited_IsRefused_BeforeAnyUpload()
    {
        var retryAfter = TimeSpan.FromHours(6);
        var fixture = CreateFixture(rateLimiter: new RateLimitedFakeRateLimiter(retryAfter));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SubmitLogoUpload.SubmitLogoUpload(SiteId, OperatorId, "image/png", Bytes(1024)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.LogoUploadRateLimited", result.Error!.Value.Code);
        Assert.Equal(0, fixture.Uploader.CallCount);
    }
}
