using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RecordAcceptance;

/// <summary>
/// `24-01`. Builds one <see cref="AcceptanceRecord"/> through the factory matching
/// <see cref="RecordAcceptance.SubjectKind"/> and saves it - always an insert
/// (<see cref="IAcceptanceRepository.SaveAsync"/>'s own remarks), never a lookup-then-update, which is
/// what keeps a second acceptance of the same document by the same subject from overwriting the
/// first: it is simply a second row, distinguishable from the first by <see cref="AcceptanceRecord.Id"/>
/// and <see cref="AcceptanceRecord.AcceptedAt"/>.
/// </summary>
public sealed class RecordAcceptanceHandler(IAcceptanceRepository acceptances, IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result<RecordedAcceptance>> HandleAsync(RecordAcceptance command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var id = new AcceptanceRecordId(idGenerator.NewId(now));

        AcceptanceRecord record;
        try
        {
            record = command.SubjectKind switch
            {
                AcceptanceSubjectKind.Tenant => AcceptanceRecord.ForTenant(
                    id, new SiteId(command.SubjectId), command.DocumentKey, command.DocumentVersion, now,
                    command.ClientIp, command.UserAgent),
                AcceptanceSubjectKind.Operator => AcceptanceRecord.ForOperator(
                    id, new OperatorId(command.SubjectId), command.DocumentKey, command.DocumentVersion, now,
                    command.ClientIp, command.UserAgent),
                AcceptanceSubjectKind.Visitor => AcceptanceRecord.ForVisitor(
                    id, new VisitorId(command.SubjectId), command.DocumentKey, command.DocumentVersion, now,
                    command.ClientIp, command.UserAgent),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(command), command.SubjectKind, "Unknown acceptance subject kind."),
            };
        }
        catch (ArgumentException ex)
        {
            return AcceptanceErrors.Invalid(ex.Message);
        }

        await acceptances.SaveAsync(record, cancellationToken);

        return new RecordedAcceptance(
            record.Id.Value, record.SubjectKind, record.SubjectId, record.DocumentKey, record.DocumentVersion, record.AcceptedAt);
    }
}
