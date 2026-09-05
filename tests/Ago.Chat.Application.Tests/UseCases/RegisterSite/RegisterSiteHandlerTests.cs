using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.RegisterSite;

public class RegisterSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string ExternalSubjectId = "keycloak-sub-1";
    private const string RequestIp = "203.0.113.5";

    private sealed record Fixture(
        RegisterSiteHandler Handler,
        FakeSiteRegistrationRepository Registrations,
        FakeRequiredDocumentRepository RequiredDocuments,
        FakeDocumentRepository Documents);

    private static Fixture CreateFixture(IRateLimiter? rateLimiter = null)
    {
        var registrations = new FakeSiteRegistrationRepository();
        var requiredDocuments = new FakeRequiredDocumentRepository();
        var documents = new FakeDocumentRepository();
        var handler = new RegisterSiteHandler(
            registrations,
            requiredDocuments,
            documents,
            rateLimiter ?? new FakeRateLimiter(),
            new RegisterSiteRateLimitOptions(),
            new FakeIdGenerator(),
            new FakeClock(Now));

        return new Fixture(handler, registrations, requiredDocuments, documents);
    }

    private static Ago.Chat.Application.UseCases.RegisterSite.RegisterSite ValidCommand() =>
        new(ExternalSubjectId, RequestIp, "Acme Support", "https://shop.example.com");

    [Fact]
    public async Task HandleAsync_WhenTheChecksPass_CreatesTheSiteBothRolesAndTheOperator_AssignedBothRoles()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Registrations.Registered);
        var registration = fixture.Registrations.Registered[0];

        Assert.Equal(result.Value.SiteId, registration.Site.Id.Value);
        Assert.Equal("Acme Support", registration.Site.Name);
        Assert.Contains("https://shop.example.com", registration.Site.AllowedOrigins);

        Assert.Equal(result.Value.OperatorId, registration.Operator.Id.Value);
        Assert.Equal(ExternalSubjectId, registration.Operator.ExternalSubjectId);
        Assert.Equal(OperatorStatus.Offline, registration.Operator.Status);

        Assert.Equal("Operator", registration.OperatorRole.Name);
        Assert.Equal(
            [
                Permission.ConversationRead.Value, Permission.ConversationSend.Value, Permission.ConversationAssign.Value,
                Permission.ConversationNoteWrite.Value, Permission.ConversationTag.Value,
                // `22-05`/`adr/0093`: the calendar's own day-to-day permissions, joined here unchanged.
                Permission.BookingConfirm.Value, Permission.BookingReject.Value, Permission.BookingCancel.Value,
                Permission.BookingMarkNoShow.Value, Permission.CustomerRead.Value, Permission.CustomerEdit.Value,
            ],
            registration.OperatorRole.Permissions);

        Assert.Equal("Admin", registration.AdminRole.Name);
        Assert.Equal(
            [
                Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value,
                Permission.SiteErase.Value, Permission.ConversationErase.Value, Permission.SiteExport.Value,
                Permission.ConversationExport.Value,
                // `22-05`/`adr/0093`: the calendar's own configuration permission, joined here unchanged.
                Permission.CalendarConfigure.Value,
                // `24-12`: the tenant's own read of who accessed their data.
            ],
            registration.AdminRole.Permissions);
    }

    /// <summary>
    /// `13-07`/`adr/0068`: the direct replacement for what used to be
    /// "...AlreadyHasAnOperatorRow_ReturnsAlreadyRegistered" - before this item, a `sub` that already
    /// resolved to *any* `operators` row was refused `409` by a pre-check this handler ran. That
    /// pre-check is gone; the whole point of `13-07` is that this now succeeds, exactly like a
    /// first-time caller, and produces a genuinely second `SiteRegistration`. Fails before this item's
    /// change (the old code returned `Site.AlreadyRegistered` here) and passes after - the regression
    /// proof this item's own "zero behavioural change for a single-tenant identity" claim does not
    /// cover, because this identity is deliberately not single-tenant.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheSubjectAlreadyHasAnOperatorRowOnADifferentSite_RegistersASecondSiteAnyway()
    {
        var fixture = CreateFixture();
        var firstResult = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);
        Assert.True(firstResult.IsSuccess);

        var secondResult = await fixture.Handler.HandleAsync(
            ValidCommand() with { SiteName = "Acme Support - Second Shop", InitialAllowedOrigin = "https://second.example.com" },
            CancellationToken.None);

        Assert.True(secondResult.IsSuccess);
        Assert.NotEqual(firstResult.Value.SiteId, secondResult.Value.SiteId);
        Assert.NotEqual(firstResult.Value.OperatorId, secondResult.Value.OperatorId);
        Assert.Equal(2, fixture.Registrations.Registered.Count);
        Assert.All(fixture.Registrations.Registered, r => Assert.Equal(ExternalSubjectId, r.Operator.ExternalSubjectId));
    }

    [Fact]
    public async Task HandleAsync_WhenTheRepositoryLosesTheUniqueIndexRace_ReturnsAlreadyRegistered()
    {
        // The pre-check above passed (no seeded operator), but a concurrent registration for the
        // same identity won the actual insert - ISiteRegistrationRepository.TryRegisterAsync's own
        // remarks on why the database's unique index, not the pre-check, is the real correctness
        // guarantee.
        var fixture = CreateFixture();
        fixture.Registrations.DenyNextRegistration = true;

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.AlreadyRegistered", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenThePerSubjectRateLimitIsExceeded_ReturnsRateLimited_WithoutRegistering()
    {
        var fixture = CreateFixture(new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyThePerIpRateLimitIsExceeded_ReturnsRateLimited_WithoutRegistering()
    {
        var fixture = CreateFixture(new SelectiveFakeRateLimiter("ip", TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteNameIsBlank_ReturnsInvalidName()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            ValidCommand() with { SiteName = "   " }, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.InvalidName", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://shop.example.com")]
    [InlineData("https://shop.example.com/path")]
    [InlineData("https://shop.example.com/")]
    public async Task HandleAsync_WhenTheInitialOriginIsMalformed_ReturnsInvalidOrigin(string origin)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            ValidCommand() with { InitialAllowedOrigin = origin }, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.InvalidOrigin", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }

    /// <summary>
    /// `24-03`'s own "if there is nothing beyond contract necessity today, say so and ship no control
    /// at all" carried through to the terms-acceptance mechanism itself: with
    /// <see cref="FakeRequiredDocumentRepository"/> seeded with nothing (its default state, matching
    /// the real `required_documents` table today), registration must behave exactly as it did before
    /// this item - zero acceptance records, nothing else about the created package changed.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenNoDocumentIsRequiredForTenants_RegistersWithNoAcceptanceRecords()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Registrations.Registered);
        Assert.Empty(fixture.Registrations.Registered[0].Acceptances);
    }

    /// <summary>
    /// `24-03`'s own Done-when #1: "Registration records an acceptance of the agreement, with its
    /// version." <see cref="FakeRequiredDocumentRepository.Require"/> is this item's own new
    /// production seam (<c>IRequiredDocumentRepository</c>) - a document required for
    /// <see cref="AcceptanceSubjectKind.Tenant"/> that has a currently published version must produce
    /// exactly one <see cref="AcceptanceRecord"/>, naming the real published version, staged in the
    /// *same* <see cref="SiteRegistration"/> package as the site itself (never a second, independent
    /// write - <c>SiteRegistration</c>'s own remarks on why).
    ///
    /// <para><b>Fails before this item's production change</b>: with `RegisterSiteHandler` reverted to
    /// its pre-`24-03` constructor (no `IRequiredDocumentRepository`/`IDocumentRepository` dependency,
    /// no acceptance-building loop), this test does not compile at all - the fixture's own
    /// `RequiredDocuments`/`Documents` fakes and `Acceptances` property have nothing to attach to. With
    /// the loop temporarily deleted but the constructor kept (the narrower regression this test
    /// actually guards day to day), <c>Registered[0].Acceptances</c> comes back empty and this
    /// assertion fails.</para>
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenATenantDocumentIsRequiredAndPublished_RecordsOneAcceptance_NamingTheCurrentVersion()
    {
        var fixture = CreateFixture();
        fixture.RequiredDocuments.Require(AcceptanceSubjectKind.Tenant, "tenant-terms");
        var document = Document.Create(new DocumentId(Guid.NewGuid()), "tenant-terms");
        document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Tenant Terms", "DRAFT v1 - awaiting legal review.", Now);
        await fixture.Documents.SaveAsync(document, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            ValidCommand() with { UserAgent = "TestBrowser/1.0" }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var registration = Assert.Single(fixture.Registrations.Registered);
        var acceptance = Assert.Single(registration.Acceptances);
        Assert.Equal(AcceptanceSubjectKind.Tenant, acceptance.SubjectKind);
        Assert.Equal(result.Value.SiteId, acceptance.SubjectId);
        Assert.Equal("tenant-terms", acceptance.DocumentKey);
        Assert.Equal("v1", acceptance.DocumentVersion);
        Assert.Equal(RequestIp, acceptance.ClientIp);
        Assert.Equal("TestBrowser/1.0", acceptance.UserAgent);
    }

    /// <summary>
    /// `24-03`: the owner declared "tenant-terms" required before publishing anything under it -
    /// `adr/0114`'s own sequencing allows exactly this ordering, and it must not silently register a
    /// tenant with no evidence of an agreement the platform itself says is required. Registration fails
    /// cleanly, before any write - <see cref="FakeSiteRegistrationRepository.Registered"/> stays empty,
    /// proving <c>TryRegisterAsync</c> was never reached.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenARequiredTenantDocumentHasNoPublishedVersion_ReturnsAgreementUnavailable_WithoutRegistering()
    {
        var fixture = CreateFixture();
        fixture.RequiredDocuments.Require(AcceptanceSubjectKind.Tenant, "tenant-terms");
        // Deliberately no call to fixture.Documents.SaveAsync - the key is required but nothing was
        // ever published under it.

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.AgreementUnavailable", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }
}
