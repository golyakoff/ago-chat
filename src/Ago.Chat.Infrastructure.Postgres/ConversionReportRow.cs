namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Dapper's raw row shape for <see cref="ConversionReportReadStore"/> - one (operator slice,
/// outcome) count. <see cref="OperatorGrouping"/> is Postgres's own <c>grouping()</c> result: <c>1</c>
/// for the site-wide grouping set (<see cref="OperatorId"/> is structurally <see langword="null"/>
/// there, regardless of the data), <c>0</c> for the per-operator grouping set (where
/// <see cref="OperatorId"/> can still be a genuine <see langword="null"/> - an outcome recorded on a
/// conversation nobody was ever assigned to). See <see cref="ConversionReportReadStore"/>'s own class
/// doc comment for why this disambiguator is needed at all.</summary>
internal sealed record ConversionReportRow(Guid? OperatorId, string Outcome, long Count, int OperatorGrouping);
