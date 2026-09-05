using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetDocumentVersion;
using Ago.Chat.Application.UseCases.PublishDocumentVersion;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `24-02`'s own three fails-before demonstrations, against a real Postgres container rather than an
/// in-memory fake - `DocumentTests` (`Ago.Chat.Domain.Tests`) already proves the same invariants at
/// the aggregate level with nothing to fake; this class proves they survive an actual round trip
/// through <see cref="DocumentRepository"/> and the real schema `Stage24AddPublishedDocuments`
/// creates.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PublishedDocumentIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 3, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishingASecondVersion_LeavesTheFirstReadableAtItsOwnIdentifier()
    {
        var documentKey = $"privacy-policy-{Guid.NewGuid():N}";

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new PublishDocumentVersionHandler(new DocumentRepository(db), new UuidV7Generator(), new SystemClock(), new NoOpCache());

            var v1 = await handler.HandleAsync(
                new PublishDocumentVersion(documentKey, "Privacy Policy", "DRAFT v1 text - awaiting legal review."),
                CancellationToken.None);
            Assert.True(v1.IsSuccess);

            var v2 = await handler.HandleAsync(
                new PublishDocumentVersion(documentKey, "Privacy Policy", "DRAFT v2 text - awaiting legal review."),
                CancellationToken.None);
            Assert.True(v2.IsSuccess);
            Assert.Equal("v1", v1.Value.Version);
            Assert.Equal("v2", v2.Value.Version);
        }

        // A fresh DbContext, a fresh repository - proving this is not the same tracked instance still
        // sitting in a change tracker's identity map, but a real row Postgres itself is holding.
        await using var verify = fixture.CreateDbContext();
        var repository = new DocumentRepository(verify);

        var readBackV1 = await repository.FindVersionAsync(documentKey, "v1", CancellationToken.None);
        var readBackV2 = await repository.FindVersionAsync(documentKey, "v2", CancellationToken.None);
        var current = await repository.FindCurrentAsync(documentKey, CancellationToken.None);

        Assert.NotNull(readBackV1);
        Assert.Equal("DRAFT v1 text - awaiting legal review.", readBackV1!.Body);
        Assert.NotNull(readBackV2);
        Assert.Equal("DRAFT v2 text - awaiting legal review.", readBackV2!.Body);
        // Publishing v2 answers "current" as v2, but never removes v1 - both this method's contract
        // and Document.Publish's own append-only invariant, now proven through real Postgres rows.
        Assert.Equal("v2", current!.Version);
    }

    [Fact]
    public async Task GetDocumentVersionHandler_AnswersBothCurrentAndASpecificVersion_WithNoCallerIdentityAtAll()
    {
        var documentKey = $"operator-terms-{Guid.NewGuid():N}";

        await using var db = fixture.CreateDbContext();
        var publishHandler = new PublishDocumentVersionHandler(new DocumentRepository(db), new UuidV7Generator(), new SystemClock(), new NoOpCache());
        await publishHandler.HandleAsync(new PublishDocumentVersion(documentKey, "Operator Terms", "DRAFT v1 text."), CancellationToken.None);
        await publishHandler.HandleAsync(new PublishDocumentVersion(documentKey, "Operator Terms", "DRAFT v2 text."), CancellationToken.None);

        // GetDocumentVersionHandler's own constructor takes no IPermissionChecker, no operator id, no
        // site id - there is no caller identity this call could name even if it wanted to. That
        // absence, not a passing authorization check, is the proof this surface needs no signed-in
        // caller (`24-02`'s own Scope).
        var readHandler = new GetDocumentVersionHandler(new DocumentRepository(db), new NoOpCache());

        var current = await readHandler.HandleAsync(new GetDocumentVersion(documentKey, null), CancellationToken.None);
        var specific = await readHandler.HandleAsync(new GetDocumentVersion(documentKey, "v1"), CancellationToken.None);
        var missing = await readHandler.HandleAsync(new GetDocumentVersion(documentKey, "v99"), CancellationToken.None);

        Assert.True(current.IsSuccess);
        Assert.Equal("v2", current.Value.Version);
        Assert.True(specific.IsSuccess);
        Assert.Equal("DRAFT v1 text.", specific.Value.Body);
        Assert.True(missing.IsFailure);
        Assert.Equal("Document.NotFound", missing.Error!.Value.Code);
    }

    [Fact]
    public async Task AnAcceptanceRecordsDocumentVersion_ResolvesToTheTextItActuallyPointsAt()
    {
        // The join `24-01` and `24-02` each leave the other to prove: an AcceptanceRecord's own
        // DocumentVersion (a bare, opaque string - `24-01`'s own remarks) is exactly the identifier
        // `24-02`'s published surface resolves. Neither item alone proves this round trip.
        var documentKey = $"processing-notice-{Guid.NewGuid():N}";
        var visitorId = new VisitorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        var publishHandler = new PublishDocumentVersionHandler(new DocumentRepository(db), new UuidV7Generator(), new SystemClock(), new NoOpCache());
        var v1 = await publishHandler.HandleAsync(
            new PublishDocumentVersion(documentKey, "Processing Notice", "DRAFT: we process your messages to answer them."),
            CancellationToken.None);
        Assert.True(v1.IsSuccess);

        var acceptance = AcceptanceRecord.ForVisitor(
            new AcceptanceRecordId(Guid.NewGuid()), visitorId, documentKey, v1.Value.Version, Now);
        await new AcceptanceRepository(db).SaveAsync(acceptance, CancellationToken.None);

        // A later reader has only what acceptance_records itself holds - DocumentKey and
        // DocumentVersion, two plain strings - and must be able to resolve them to real text with
        // nothing else.
        var resolved = await new DocumentRepository(db).FindVersionAsync(
            acceptance.DocumentKey, acceptance.DocumentVersion, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("DRAFT: we process your messages to answer them.", resolved!.Body);
    }

    [Fact]
    public async Task PublishDocumentVersion_RacedOnceByAConcurrentPublishForTheSameKey_RetriesAndSucceeds()
    {
        var documentKey = $"privacy-policy-{Guid.NewGuid():N}";

        // Seeded first, outside the race: the scenario this proves is two publishes racing over an
        // *existing* Document row's own xmin (an UPDATE conflict) - the identical shape
        // ConversationConcurrencyConflictTests races an assign/close against an already-seeded
        // conversation, never a conversation's own first insert. Racing the very first publish for a
        // brand-new key would instead collide on ix_documents_key's own uniqueness (a different,
        // already-prevented failure mode - AlreadyRegistered's own shape - not the xmin path this test
        // exists to exercise).
        await using (var seed = fixture.CreateDbContext())
        {
            var seedHandler = new PublishDocumentVersionHandler(new DocumentRepository(seed), new UuidV7Generator(), new SystemClock(), new NoOpCache());
            var seeded = await seedHandler.HandleAsync(
                new PublishDocumentVersion(documentKey, "Privacy Policy", "DRAFT: the seeded version."), CancellationToken.None);
            Assert.True(seeded.IsSuccess);
        }

        await using var db = fixture.CreateDbContext();
        var racingRepository = new RacingDocumentRepository(
            new DocumentRepository(db), maxInjections: 1, () => PublishConcurrentlyAsync(documentKey));
        var handler = new PublishDocumentVersionHandler(racingRepository, new UuidV7Generator(), new SystemClock(), new NoOpCache());

        var result = await handler.HandleAsync(
            new PublishDocumentVersion(documentKey, "Privacy Policy", "DRAFT: the version that wins the race."), CancellationToken.None);

        // The clean outcome `24-02` asks for: a transparent retry against the now-fresh Document row,
        // not the DbUpdateConcurrencyException that would otherwise reach a caller as a raw 500 - the
        // identical shape ConversationConcurrencyConflictTests already proves for Conversation.
        Assert.True(result.IsSuccess);
        Assert.Equal(2, racingRepository.SaveAttempts);
        Assert.Equal("v3", result.Value.Version); // v1 seeded, v2 was the concurrent writer's own publish.

        await using var verify = fixture.CreateDbContext();
        var document = await new DocumentRepository(verify).GetByKeyAsync(documentKey, CancellationToken.None);
        Assert.Equal(3, document!.Versions.Count);
    }

    private async Task PublishConcurrentlyAsync(string documentKey)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new PublishDocumentVersionHandler(new DocumentRepository(db), new UuidV7Generator(), new SystemClock(), new NoOpCache());
        var result = await handler.HandleAsync(new PublishDocumentVersion(documentKey, "Privacy Policy", "DRAFT: the concurrent writer's version."), CancellationToken.None);
        Assert.True(result.IsSuccess);
    }

    /// <summary>Delegates every read to a real <see cref="DocumentRepository"/> untouched; on
    /// <see cref="SaveAsync"/>, for each of the first <paramref name="maxInjections"/> calls, runs
    /// <paramref name="injectConcurrentPublishAsync"/> to completion (a real commit, on a different
    /// <see cref="Persistence.AgoChatDbContext"/>) before delegating to the real save - the identical
    /// seam <c>ConversationConcurrencyConflictTests.RacingConversationRepository</c> already
    /// establishes, restated here for <see cref="Document"/>.</summary>
    private sealed class RacingDocumentRepository(
        IDocumentRepository inner, int maxInjections, Func<Task> injectConcurrentPublishAsync) : IDocumentRepository
    {
        private int _saveAttempts;

        public int SaveAttempts => _saveAttempts;

        public Task<Document?> GetByKeyAsync(string documentKey, CancellationToken cancellationToken) =>
            inner.GetByKeyAsync(documentKey, cancellationToken);

        public async Task SaveAsync(Document document, CancellationToken cancellationToken)
        {
            _saveAttempts++;
            if (_saveAttempts <= maxInjections)
            {
                await injectConcurrentPublishAsync();
            }

            await inner.SaveAsync(document, cancellationToken);
        }

        public Task<PublishedDocumentVersion?> FindVersionAsync(string documentKey, string version, CancellationToken cancellationToken) =>
            inner.FindVersionAsync(documentKey, version, cancellationToken);

        public Task<PublishedDocumentVersion?> FindCurrentAsync(string documentKey, CancellationToken cancellationToken) =>
            inner.FindCurrentAsync(documentKey, cancellationToken);
    }
}
