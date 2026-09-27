namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-237`/`adr/0186` §7: one grouped row of the conversion read over <c>analytics_daily_rollups</c> - a
/// <c>(dimension_type, dimension_key)</c> pair with the four outcome counts summed across every
/// <c>local_day</c> in the requested window. Only the <c>total</c> and <c>operator</c> dimensions are read
/// (the conversion report has a site-wide bucket and a per-operator breakdown, nothing else); the
/// recorded count and rate are reconstituted from these counts by
/// <see cref="RollupConversionReportReadStore"/>, never stored (design §7: the read reconstitutes the
/// ratio, never the store).
/// </summary>
/// <param name="DimensionType">One of <see cref="RollupDimensionTypes"/>' values - here always
/// <see cref="RollupDimensionTypes.Total"/> or <see cref="RollupDimensionTypes.Operator"/>.</param>
/// <param name="DimensionKey">Empty string for the total row; the operator id as text for an operator row.</param>
/// <param name="ConvertedCount">Conversations recorded <c>Converted</c>, summed across the window.</param>
/// <param name="NotConvertedCount">Conversations recorded <c>NotConverted</c>, summed.</param>
/// <param name="FollowUpCount">Conversations recorded <c>FollowUpNeeded</c>, summed - excluded from the
/// rate's numerator and denominator alike.</param>
/// <param name="UnsetCount">Conversations with no outcome recorded, summed - the coverage signal, also
/// excluded from the rate's denominator.</param>
internal sealed record ConversionRollupRow(
    string DimensionType,
    string DimensionKey,
    long ConvertedCount,
    long NotConvertedCount,
    long FollowUpCount,
    long UnsetCount);
