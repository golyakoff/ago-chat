using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-05`/`adr/0093`: the account side's own half of the projection - proves the fact
/// <see cref="RoleAssignmentsChanged"/> carries is staged in the *same* transaction as the state change
/// it describes (rule 4), for both directions this item's Done-when names: a grant (site registration)
/// and a revocation (operator removal, published as the identical fact with an empty permission set -
/// see that event's own remarks for why this is not a second kind of event). The consumer half - the
/// idempotent upsert into AGO Calendar's own projection table - lives in `ago-calendar`, a different
/// repository with a different database; this file cannot reach it and does not try to.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleAssignmentsChangedOutboxTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task RegisteringASite_StagesOneRoleAssignmentsChangedRow_CarryingTheOwnersUnionOfBothSeededRoles()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var operatorRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new SiteRegistrationRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(Now));

            var registered = await repository.TryRegisterAsync(
                new SiteRegistration(
                    new Site(siteId, $"site_{siteId.Value:N}", []),
                    new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId),
                    new RoleSeed(operatorRoleId, "Operator", [Permission.ConversationRead.Value, Permission.BookingConfirm.Value]),
                    new RoleSeed(adminRoleId, "Admin", [Permission.SiteConfigure.Value, Permission.CalendarConfigure.Value])),
                CancellationToken.None);

            Assert.True(registered);
        }

        await using var verify = fixture.CreateDbContext();
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None);

        var contract = System.Text.Json.JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Equal(externalSubjectId, contract.ExternalSubjectId);
        Assert.Equal(siteId.Value, contract.SiteId);
        var expectedPermissions = new[]
        {
            Permission.ConversationRead.Value, Permission.BookingConfirm.Value,
            Permission.SiteConfigure.Value, Permission.CalendarConfigure.Value,
        };
        Assert.Equal(
            expectedPermissions.OrderBy(p => p, StringComparer.Ordinal),
            contract.Permissions.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Null(outboxRow.PublishedAt);
    }

    [Fact]
    public async Task RemovingAnOperator_StagesARoleAssignmentsChangedRow_WithAnEmptyPermissionSet_TheSameFactBecomingNothing()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var requestedById = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId));
            seed.Operators.Add(new Operator(requestedById, siteId, OperatorStatus.Offline, capacity: 5, "sub-admin"));
            seed.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteManageOperators.Value] });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = requestedById, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new RemoveOperatorHandler(
                new OperatorRepository(db), new PermissionChecker(db), new EfUnitOfWork(db),
                new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(Now));

            var result = await handler.HandleAsync(new RemoveOperator(requestedById, siteId, operatorId), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None);

        var contract = System.Text.Json.JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Empty(contract.Permissions);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
