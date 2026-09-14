using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Chat.Application.UseCases.RecordAcceptance;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.AiAddOn;

/// <summary>
/// `25-04`: the four preconditions on enabling, and - the item's hardest requirement - the proof that
/// <b>accepting the agreement and declaring a lawful basis are two separate facts</b>, with a state in
/// which each exists without the other and a different refusal for each.
///
/// <para>The acceptance side runs through the real `24-01` machinery
/// (<see cref="RecordAcceptanceHandler"/> over <see cref="FakeAcceptanceRepository"/>), never a stub
/// that says "yes": a test whose acceptance check is a boolean nobody writes would pass against a
/// handler that had forgotten to look at the version at all.</para>
/// </summary>
public sealed class EnableAiAddOnHandlerTests
{
    private static readonly SiteId Site = new(Guid.NewGuid());
    private static readonly OperatorId Actor = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Enable_RefusesWhenTheSiteHasNotBoughtTheAddOn()
    {
        var f = new Fixture();
        await f.PublishAgreementAsync("v1");
        await f.AcceptAsync("v1");
        await f.DeclareAsync();
        // No module quantity granted.

        var result = await f.Handler.HandleAsync(new EnableAiAddOn(Site, Actor), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AiAddOn.NotPurchased", result.Error!.Value.Code);
    }

    /// <summary>The item's own Done-when: enabling is refused until the current version of the
    /// agreement is accepted. Declared but not accepted - one of the two asymmetric states.</summary>
    [Fact]
    public async Task Enable_RefusesWhenTheTenantHasDeclaredButNotAccepted()
    {
        var f = new Fixture();
        f.Purchase();
        await f.PublishAgreementAsync("v1");
        await f.DeclareAsync();

        var result = await f.Handler.HandleAsync(new EnableAiAddOn(Site, Actor), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AiAddOn.AgreementNotAccepted", result.Error!.Value.Code);
    }

    /// <summary>The mirror image, and together with the test above this is the distinctness proof: the
    /// system tells the two missing facts apart rather than collapsing them into one "not ready".</summary>
    [Fact]
    public async Task Enable_RefusesWhenTheTenantHasAcceptedButNotDeclared()
    {
        var f = new Fixture();
        f.Purchase();
        await f.PublishAgreementAsync("v1");
        await f.AcceptAsync("v1");

        var result = await f.Handler.HandleAsync(new EnableAiAddOn(Site, Actor), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AiAddOn.BasisNotDeclared", result.Error!.Value.Code);
        // And the acceptance really did land - this is not "nothing happened", it is "one of the two
        // happened", which is the state the schema has to be able to represent.
        var accepted = await f.Acceptances.GetForSubjectAsync(AcceptanceSubjectKind.Tenant, Site.Value, CancellationToken.None);
        Assert.Single(accepted);
        Assert.Empty(f.Declarations.Saved);
    }

    /// <summary>The acceptance names the version - so an acceptance of v1 does not satisfy a site whose
    /// current agreement is v2.</summary>
    [Fact]
    public async Task Enable_RefusesWhenOnlyAnOlderVersionWasAccepted()
    {
        var f = new Fixture();
        f.Purchase();
        await f.PublishAgreementAsync("v1");
        await f.AcceptAsync("v1");
        await f.DeclareAsync();
        await f.PublishAgreementAsync("v2");

        var result = await f.Handler.HandleAsync(new EnableAiAddOn(Site, Actor), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AiAddOn.AgreementNotAccepted", result.Error!.Value.Code);
    }

    /// <summary>Both facts present: the add-on turns on, the cut-off is the clock's own instant, and the
    /// enablement names the version that was accepted.</summary>
    [Fact]
    public async Task Enable_SucceedsWithBothFacts_AndRecordsTheCutOffAndTheAcceptedVersion()
    {
        var f = new Fixture();
        f.Purchase();
        await f.PublishAgreementAsync("v1");
        await f.AcceptAsync("v1");
        await f.DeclareAsync();

        var result = await f.Handler.HandleAsync(new EnableAiAddOn(Site, Actor), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, result.Value.EffectiveFrom);
        Assert.Equal("v1", result.Value.AcceptedDocumentVersion);
        Assert.Equal(AiAddOnAgreement.DocumentKey, result.Value.AcceptedDocumentKey);

        var stored = await f.Enablements.GetForSiteAsync(Site, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.True(stored!.IsEnabled);
        Assert.Equal(Now, stored.EffectiveFrom);
        Assert.Equal(Actor, stored.EnabledBy);
    }

    /// <summary>The two facts stay separately timestamped and separately attributed all the way to the
    /// end - the item's third Done-when made visible: an acceptance carries the *tenant* as its subject
    /// and its own instant; the declaration carries the *operator* and a different instant.</summary>
    [Fact]
    public async Task TheAcceptanceAndTheDeclarationAreSeparatelyTimestampedAndAttributed()
    {
        var f = new Fixture();
        f.Purchase();
        await f.PublishAgreementAsync("v1");
        await f.AcceptAsync("v1");
        f.Clock.Advance(TimeSpan.FromMinutes(7));
        await f.DeclareAsync();

        var accepted = Assert.Single(
            await f.Acceptances.GetForSubjectAsync(AcceptanceSubjectKind.Tenant, Site.Value, CancellationToken.None));
        var declared = Assert.Single(f.Declarations.Saved);

        Assert.Equal(AcceptanceSubjectKind.Tenant, accepted.SubjectKind);
        Assert.Equal(Site.Value, accepted.SubjectId);
        Assert.Equal(Now, accepted.AcceptedAt);

        Assert.Equal(Site, declared.SiteId);
        Assert.Equal(Actor, declared.DeclaredBy);
        Assert.Equal(Now.AddMinutes(7), declared.DeclaredAt);

        // The point of the whole design: the two facts do not share an instant, and the declaration
        // names a person while the acceptance names the company.
        Assert.NotEqual(accepted.AcceptedAt, declared.DeclaredAt);
        Assert.NotEqual(accepted.SubjectId, declared.DeclaredBy.Value);
    }

    [Fact]
    public async Task Enable_RefusesAnOperatorWithoutSiteConfigure()
    {
        var f = new Fixture(grantPermission: false);
        f.Purchase();
        await f.PublishAgreementAsync("v1");

        var result = await f.Handler.HandleAsync(new EnableAiAddOn(Site, Actor), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AiAddOn.Forbidden", result.Error!.Value.Code);
    }

    private sealed class Fixture
    {
        public Fixture(bool grantPermission = true)
        {
            if (grantPermission)
            {
                Permissions.Grant(Actor, Site, Permission.SiteConfigure);
            }

            var recordAcceptance = new RecordAcceptanceHandler(Acceptances, new FakeIdGenerator(), Clock);
            Accept = new AcceptAiAddOnAgreementHandler(Permissions, Documents, recordAcceptance);
            Declare = new DeclareAiProcessingBasisHandler(Permissions, Declarations, new FakeIdGenerator(), Clock);
            Handler = new EnableAiAddOnHandler(
                Permissions, Grants, Documents, Acceptances, Declarations, Enablements,
                new AiAddOnOptions { ModuleKey = AiGates.ModuleKey }, Clock);
        }

        public FakePermissionChecker Permissions { get; } = new();

        public FakeModuleQuantityGrantStore Grants { get; } = new();

        public FakeDocumentRepository Documents { get; } = new();

        public FakeAcceptanceRepository Acceptances { get; } = new();

        public FakeAiProcessingBasisDeclarationRepository Declarations { get; } = new();

        public FakeAiAddOnEnablementRepository Enablements { get; } = new();

        public AdvanceableClock Clock { get; } = new(Now);

        public EnableAiAddOnHandler Handler { get; }

        public AcceptAiAddOnAgreementHandler Accept { get; }

        public DeclareAiProcessingBasisHandler Declare { get; }

        public void Purchase() => Grants.Grants[(Site, new ModuleKey(AiGates.ModuleKey))] = 1;

        /// <summary>Publishes the next version of the real `24-02` aggregate, so `v1`/`v2` are the
        /// spellings <c>Document.Publish</c> itself derives rather than strings this test invents.</summary>
        public async Task PublishAgreementAsync(string expectedVersion)
        {
            var document = await Documents.GetByKeyAsync(AiAddOnAgreement.DocumentKey, CancellationToken.None)
                ?? Document.Create(new DocumentId(Guid.NewGuid()), AiAddOnAgreement.DocumentKey);
            var version = document.Publish(
                new PublishedDocumentVersionId(Guid.NewGuid()), "AI addendum", "the text", Now);
            Assert.Equal(expectedVersion, version.Version);
            await Documents.SaveAsync(document, CancellationToken.None);
        }

        public async Task AcceptAsync(string version)
        {
            var result = await Accept.HandleAsync(
                new AcceptAiAddOnAgreement(Site, Actor, version), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        public async Task DeclareAsync()
        {
            var result = await Declare.HandleAsync(new DeclareAiProcessingBasis(Site, Actor), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }
    }

    /// <summary>A clock a test can move - the acceptance and the declaration must be able to happen at
    /// two different instants, which is the whole point of them being two facts.</summary>
    private sealed class AdvanceableClock(DateTimeOffset start) : Ago.Platform.Kernel.IClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
