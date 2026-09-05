using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetRequiredDocumentsForSubjectKind;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetRequiredDocumentsForSubjectKind;

public class GetRequiredDocumentsForSubjectKindHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(GetRequiredDocumentsForSubjectKindHandler Handler, FakeRequiredDocumentRepository RequiredDocuments, FakeDocumentRepository Documents);

    private static Fixture CreateFixture()
    {
        var requiredDocuments = new FakeRequiredDocumentRepository();
        var documents = new FakeDocumentRepository();
        return new Fixture(new GetRequiredDocumentsForSubjectKindHandler(requiredDocuments, documents), requiredDocuments, documents);
    }

    [Fact]
    public async Task HandleAsync_WhenNothingIsRequired_ReturnsAnEmptyList()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetRequiredDocumentsForSubjectKind.GetRequiredDocumentsForSubjectKind(AcceptanceSubjectKind.Tenant),
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task HandleAsync_WhenARequiredDocumentIsPublished_ReturnsItsCurrentVersionAndTitle()
    {
        var fixture = CreateFixture();
        fixture.RequiredDocuments.Require(AcceptanceSubjectKind.Tenant, "tenant-terms");
        var document = Document.Create(new DocumentId(Guid.NewGuid()), "tenant-terms");
        document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Tenant Terms", "DRAFT v1.", Now);
        document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Tenant Terms", "DRAFT v2.", Now);
        await fixture.Documents.SaveAsync(document, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetRequiredDocumentsForSubjectKind.GetRequiredDocumentsForSubjectKind(AcceptanceSubjectKind.Tenant),
            CancellationToken.None);

        var summary = Assert.Single(result);
        Assert.Equal("tenant-terms", summary.DocumentKey);
        Assert.Equal("v2", summary.Version);
        Assert.Equal("Tenant Terms", summary.Title);
        Assert.NotNull(summary.PublishedAt);
    }

    /// <summary>
    /// `24-03`: the case a `Site.AgreementUnavailable` registration failure would come from - a
    /// required key with nothing published under it yet. This read never fails for it; it reports the
    /// gap as data (null fields) instead, so a caller can render "required, not yet readable" rather
    /// than treating the whole list request as an error over one misconfigured key.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenARequiredDocumentHasNoPublishedVersion_ReturnsItWithNullFields()
    {
        var fixture = CreateFixture();
        fixture.RequiredDocuments.Require(AcceptanceSubjectKind.Tenant, "tenant-terms");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetRequiredDocumentsForSubjectKind.GetRequiredDocumentsForSubjectKind(AcceptanceSubjectKind.Tenant),
            CancellationToken.None);

        var summary = Assert.Single(result);
        Assert.Equal("tenant-terms", summary.DocumentKey);
        Assert.Null(summary.Version);
        Assert.Null(summary.Title);
        Assert.Null(summary.PublishedAt);
    }

    [Fact]
    public async Task HandleAsync_OnlyReturnsDocumentsRequiredForTheRequestedSubjectKind()
    {
        var fixture = CreateFixture();
        fixture.RequiredDocuments.Require(AcceptanceSubjectKind.Operator, "operator-terms");
        var document = Document.Create(new DocumentId(Guid.NewGuid()), "operator-terms");
        document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Operator Terms", "DRAFT v1.", Now);
        await fixture.Documents.SaveAsync(document, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetRequiredDocumentsForSubjectKind.GetRequiredDocumentsForSubjectKind(AcceptanceSubjectKind.Tenant),
            CancellationToken.None);

        Assert.Empty(result);
    }
}
