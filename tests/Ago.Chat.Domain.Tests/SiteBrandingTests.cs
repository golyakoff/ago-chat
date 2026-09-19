namespace Ago.Chat.Domain.Tests;

/// <summary>`25-160`: `Site`'s own reply-email brand write paths - a separate file from
/// `SiteTests.cs` (already 800+ lines) for one genuinely separable concern, the same "one file per
/// real seam, not by mechanical size" judgement this codebase's own file layout already follows
/// elsewhere.</summary>
public class SiteBrandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UpdateBrandCompanyName_WhenCalled_SetsTheValueAndRaisesTheEvent()
    {
        var id = new SiteId(Guid.NewGuid());
        var site = new Site(id, "shop_7f3a", []);

        site.UpdateBrandCompanyName("Acme Repairs LLC", Now);

        Assert.Equal("Acme Repairs LLC", site.BrandCompanyName);
        var raised = Assert.IsType<SiteBrandCompanyNameUpdated>(Assert.Single(site.DomainEvents));
        Assert.Equal(id, raised.SiteId);
        Assert.Equal(Now, raised.OccurredAt);
    }

    [Fact]
    public void SubmitLogoUpload_WhenCalled_DoesNotTouchTheCurrentReadyLogo()
    {
        // `25-160`: the central invariant Site.LogoObjectKey's own remarks state - a replacement
        // upload in flight must never make a tenant's already-working logo disappear from outbound
        // email while it is being validated.
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        const string firstPending = "pending/first.png";
        const string firstPublic = "logo/first.png";
        site.SubmitLogoUpload(firstPending, "image/png", Now);
        site.PromoteLogo(firstPending, firstPublic, Now);
        site.ClearDomainEvents();

        site.SubmitLogoUpload("pending/second.png", "image/png", Now);

        Assert.Equal(firstPublic, site.LogoObjectKey);
        Assert.True(site.HasLogo);
        Assert.Equal(LogoStatus.Pending, site.LogoStatus);
    }

    [Fact]
    public void SubmitLogoUpload_WithAnEmptyObjectKey_Throws()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        Assert.Throws<ArgumentException>(() => site.SubmitLogoUpload("   ", "image/png", Now));
    }

    [Fact]
    public void PromoteLogo_WhenTheKeyMatchesThePendingUpload_PromotesItAndRaisesTheEvent()
    {
        var id = new SiteId(Guid.NewGuid());
        var site = new Site(id, "shop_7f3a", []);
        const string pending = "pending/one.png";
        site.SubmitLogoUpload(pending, "image/png", Now);
        site.ClearDomainEvents();

        site.PromoteLogo(pending, "logo/one.png", Now);

        Assert.Equal(LogoStatus.Ready, site.LogoStatus);
        Assert.Equal("logo/one.png", site.LogoObjectKey);
        Assert.Null(site.LogoRejectionReason);
        var raised = Assert.IsType<SiteLogoPromoted>(Assert.Single(site.DomainEvents));
        Assert.Equal(id, raised.SiteId);
    }

    [Fact]
    public void PromoteLogo_WhenAlreadySupersededByANewerUpload_IsANoOp()
    {
        // `25-160`: the staleness guard PromoteLogo's own remarks describe - an out-of-order success
        // for an upload that is no longer the current pending one must change nothing.
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        const string firstPending = "pending/first.png";
        site.SubmitLogoUpload(firstPending, "image/png", Now);
        site.ClearDomainEvents();
        site.SubmitLogoUpload("pending/second.png", "image/png", Now);
        site.ClearDomainEvents();

        site.PromoteLogo(firstPending, "logo/first.png", Now);

        Assert.False(site.HasLogo);
        Assert.Equal(LogoStatus.Pending, site.LogoStatus);
        Assert.Empty(site.DomainEvents);
    }

    [Fact]
    public void RejectLogoUpload_WhenTheKeyMatchesThePendingUpload_RecordsTheReason_AndRaisesNoEvent()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        const string pending = "pending/one.png";
        site.SubmitLogoUpload(pending, "image/png", Now);
        site.ClearDomainEvents();

        site.RejectLogoUpload(pending, "Wrong dimensions.", Now);

        Assert.Equal(LogoStatus.Rejected, site.LogoStatus);
        Assert.Equal("Wrong dimensions.", site.LogoRejectionReason);
        Assert.False(site.HasLogo);
        Assert.Empty(site.DomainEvents);
    }

    [Fact]
    public void RejectLogoUpload_WhenAlreadySupersededByANewerUpload_IsANoOp()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        const string firstPending = "pending/first.png";
        site.SubmitLogoUpload(firstPending, "image/png", Now);
        site.SubmitLogoUpload("pending/second.png", "image/png", Now);
        site.ClearDomainEvents();

        site.RejectLogoUpload(firstPending, "Wrong dimensions.", Now);

        Assert.Equal(LogoStatus.Pending, site.LogoStatus);
        Assert.Null(site.LogoRejectionReason);
    }

    [Fact]
    public void RejectLogoUpload_AfterAPreviousLogoWasReady_LeavesTheReadyLogoUntouched()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        const string firstPending = "pending/first.png";
        const string firstPublic = "logo/first.png";
        site.SubmitLogoUpload(firstPending, "image/png", Now);
        site.PromoteLogo(firstPending, firstPublic, Now);
        const string secondPending = "pending/second.png";
        site.SubmitLogoUpload(secondPending, "image/png", Now);
        site.ClearDomainEvents();

        site.RejectLogoUpload(secondPending, "Animated images are not supported.", Now);

        Assert.Equal(LogoStatus.Rejected, site.LogoStatus);
        Assert.Equal(firstPublic, site.LogoObjectKey);
        Assert.True(site.HasLogo);
    }
}
