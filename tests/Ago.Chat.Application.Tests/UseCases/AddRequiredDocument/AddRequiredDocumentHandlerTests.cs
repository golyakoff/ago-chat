using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AddRequiredDocument;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.AddRequiredDocument;

/// <summary>`24-16`: the platform owner's own write, at the handler level - the fast, fake-backed
/// proof of validation and idempotency; <c>RequiredDocumentOwnerEndpointsTests</c>
/// (`Ago.Chat.Integration.Tests`) carries the real-Postgres and real-HTTP round trip this level cannot
/// see (`24-03`'s own reason a handler-level test alone was never going to catch this item's whole
/// gap).</summary>
public class AddRequiredDocumentHandlerTests
{
    private static AddRequiredDocumentHandler CreateHandler(out FakeRequiredDocumentRepository repository)
    {
        repository = new FakeRequiredDocumentRepository();
        return new AddRequiredDocumentHandler(repository);
    }

    [Fact]
    public async Task HandleAsync_ANewPair_IsAdded_AndListedAfterwards()
    {
        var handler = CreateHandler(out var repository);

        var result = await handler.HandleAsync(
            new Application.UseCases.AddRequiredDocument.AddRequiredDocument(AcceptanceSubjectKind.Tenant, "tenant-terms"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.AlreadyRequired);
        var keys = await repository.GetRequiredDocumentKeysAsync(AcceptanceSubjectKind.Tenant, CancellationToken.None);
        Assert.Equal(["tenant-terms"], keys);
    }

    /// <summary>Idempotent - the second identical add is not an error, and reports
    /// <see cref="AddedRequiredDocument.AlreadyRequired"/> rather than duplicating the row (the real
    /// repository's own unique index would refuse a duplicate row outright; this handler-level test
    /// proves the handler's own contract, the fake repository's own set-membership semantics matching
    /// <c>Ago.Chat.Infrastructure.Postgres.RequiredDocumentRepository.AddAsync</c>'s own).</summary>
    [Fact]
    public async Task HandleAsync_APairAlreadyRequired_ReportsAlreadyRequired_AndDoesNotDuplicate()
    {
        var handler = CreateHandler(out var repository);
        await handler.HandleAsync(
            new Application.UseCases.AddRequiredDocument.AddRequiredDocument(AcceptanceSubjectKind.Tenant, "tenant-terms"),
            CancellationToken.None);

        var second = await handler.HandleAsync(
            new Application.UseCases.AddRequiredDocument.AddRequiredDocument(AcceptanceSubjectKind.Tenant, "tenant-terms"),
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.True(second.Value.AlreadyRequired);
        var keys = await repository.GetRequiredDocumentKeysAsync(AcceptanceSubjectKind.Tenant, CancellationToken.None);
        Assert.Equal(["tenant-terms"], keys);
    }

    [Fact]
    public async Task HandleAsync_TheIdenticalKeyForADifferentSubjectKind_IsARealDistinctPair()
    {
        var handler = CreateHandler(out var repository);
        await handler.HandleAsync(
            new Application.UseCases.AddRequiredDocument.AddRequiredDocument(AcceptanceSubjectKind.Tenant, "shared-terms"),
            CancellationToken.None);

        var result = await handler.HandleAsync(
            new Application.UseCases.AddRequiredDocument.AddRequiredDocument(AcceptanceSubjectKind.Operator, "shared-terms"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.AlreadyRequired);
    }

    [Fact]
    public async Task HandleAsync_AnEmptyDocumentKey_IsRejected_AndAddsNothing()
    {
        var handler = CreateHandler(out var repository);

        var result = await handler.HandleAsync(
            new Application.UseCases.AddRequiredDocument.AddRequiredDocument(AcceptanceSubjectKind.Tenant, "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("RequiredDocument.Invalid", result.Error!.Value.Code);
        var keys = await repository.GetRequiredDocumentKeysAsync(AcceptanceSubjectKind.Tenant, CancellationToken.None);
        Assert.Empty(keys);
    }

    [Fact]
    public async Task HandleAsync_ADocumentKeyExceedingTheMaxLength_IsRejected()
    {
        var handler = CreateHandler(out _);
        var tooLong = new string('a', Document.MaxDocumentKeyLength + 1);

        var result = await handler.HandleAsync(
            new Application.UseCases.AddRequiredDocument.AddRequiredDocument(AcceptanceSubjectKind.Tenant, tooLong),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("RequiredDocument.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ADocumentKeyWithSurroundingWhitespace_IsTrimmedBeforeStoring()
    {
        var handler = CreateHandler(out var repository);

        var result = await handler.HandleAsync(
            new Application.UseCases.AddRequiredDocument.AddRequiredDocument(AcceptanceSubjectKind.Tenant, "  tenant-terms  "),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("tenant-terms", result.Value.DocumentKey);
        var keys = await repository.GetRequiredDocumentKeysAsync(AcceptanceSubjectKind.Tenant, CancellationToken.None);
        Assert.Equal(["tenant-terms"], keys);
    }
}
