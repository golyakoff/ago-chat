using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RemoveRequiredDocument;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RemoveRequiredDocument;

/// <summary>`24-16`: the withdrawal half, at the handler level. The `adr/0111` claim itself - that
/// removing a requirement leaves an already-recorded acceptance untouched - cannot be proven against
/// this fake (it has no `acceptance_records` to touch in the first place); that proof is
/// `RequiredDocumentOwnerEndpointsTests.RemovingARequirement_LeavesAnAlreadyRecordedAcceptanceUntouched`
/// (`Ago.Chat.Integration.Tests`), against a real Postgres. This class covers what a handler-level test
/// can actually see: validation and idempotency.</summary>
public class RemoveRequiredDocumentHandlerTests
{
    private static RemoveRequiredDocumentHandler CreateHandler(out FakeRequiredDocumentRepository repository)
    {
        repository = new FakeRequiredDocumentRepository();
        return new RemoveRequiredDocumentHandler(repository);
    }

    [Fact]
    public async Task HandleAsync_ARequiredPair_IsRemoved_AndNoLongerListed()
    {
        var repository = new FakeRequiredDocumentRepository();
        repository.Require(AcceptanceSubjectKind.Tenant, "tenant-terms");
        var handler = new RemoveRequiredDocumentHandler(repository);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveRequiredDocument.RemoveRequiredDocument(AcceptanceSubjectKind.Tenant, "tenant-terms"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.WasRequired);
        var keys = await repository.GetRequiredDocumentKeysAsync(AcceptanceSubjectKind.Tenant, CancellationToken.None);
        Assert.Empty(keys);
    }

    /// <summary>Idempotent - removing a pair that was never required is not an error, and reports
    /// <see cref="RemovedRequiredDocument.WasRequired"/> <see langword="false"/> rather than throwing.</summary>
    [Fact]
    public async Task HandleAsync_APairThatWasNeverRequired_ReportsWasRequiredFalse_AndIsNotAnError()
    {
        var handler = CreateHandler(out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveRequiredDocument.RemoveRequiredDocument(AcceptanceSubjectKind.Tenant, "never-required"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.WasRequired);
    }

    [Fact]
    public async Task HandleAsync_RemovingForOneSubjectKind_LeavesTheIdenticalKeyRequiredForAnother()
    {
        var repository = new FakeRequiredDocumentRepository();
        repository.Require(AcceptanceSubjectKind.Tenant, "shared-terms");
        repository.Require(AcceptanceSubjectKind.Operator, "shared-terms");
        var handler = new RemoveRequiredDocumentHandler(repository);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveRequiredDocument.RemoveRequiredDocument(AcceptanceSubjectKind.Tenant, "shared-terms"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.WasRequired);
        Assert.Empty(await repository.GetRequiredDocumentKeysAsync(AcceptanceSubjectKind.Tenant, CancellationToken.None));
        Assert.Equal(
            ["shared-terms"], await repository.GetRequiredDocumentKeysAsync(AcceptanceSubjectKind.Operator, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_AnEmptyDocumentKey_IsRejected()
    {
        var handler = CreateHandler(out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RemoveRequiredDocument.RemoveRequiredDocument(AcceptanceSubjectKind.Tenant, "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("RequiredDocument.Invalid", result.Error!.Value.Code);
    }
}
