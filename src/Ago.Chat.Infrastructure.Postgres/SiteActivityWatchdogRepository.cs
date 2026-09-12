using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-73`: <see cref="ISiteActivityWatchdog"/>'s own connection, for the one call site that does not
/// already have one open of its own - <c>Ago.Chat.Api.Auth.OperatorIdentityClaimsTransformation</c>'s
/// login/authenticated-request hook. Opens and disposes its own short-lived connection per call, the
/// same shape <c>ErasureRequestRepository</c> already uses for a single-statement, no-surrounding-
/// transaction write.
/// </summary>
public sealed class SiteActivityWatchdogRepository(
    NpgsqlDataSource dataSource, IOptions<SiteActivityWatchdogOptions> options) : ISiteActivityWatchdog
{
    public async Task TouchAsync(SiteId siteId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await SiteActivityWatchdogQuery.TouchAsync(
            connection, transaction: null, siteId.Value, at, options.Value.MinTouchInterval, cancellationToken);
    }
}
