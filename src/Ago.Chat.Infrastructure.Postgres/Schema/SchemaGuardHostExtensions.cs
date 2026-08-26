using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>
/// `8-08`: the one line each serving host adds, and the reason it goes where it does.
///
/// <para><b>Called between <c>builder.Build()</c> and <c>app.Run()</c>, not as an
/// <c>IHostedService</c>.</b> That looks like the less idiomatic choice and it is the only correct
/// one here: <c>GenericWebHostService</c> - the hosted service that opens the listening socket - is
/// registered by <c>WebApplication.CreateBuilder</c> itself, so it starts *before* anything
/// registered afterwards. A hosted service that threw would do so with the port already open and
/// requests already arriving, which is precisely the "serves 200s it should not" state this item
/// exists to close. An explicit <c>await</c> before <c>Run()</c> has no such ordering to reason
/// about.</para>
/// </summary>
public static class SchemaGuardHostExtensions
{
    /// <summary>
    /// Refuses to return while the database is behind this build, and throws once it has waited long
    /// enough (<see cref="SchemaGuardOptions"/>).
    ///
    /// <para>Its own DI scope: <c>AgoChatDbContext</c> is scoped, and the root provider cannot resolve
    /// a scoped service.</para>
    /// </summary>
    public static async Task EnsureSchemaIsCurrentAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<SchemaGuardOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SchemaVersionGuard));

        if (!options.Enabled)
        {
            logger.LogWarning(
                "SchemaGuard:Enabled is false - this host will start without checking that the database "
                + "matches the migrations it was built against (adr/0056, 8-08).");
            return;
        }

        await SchemaVersionGuard.EnsureCurrentAsync(
            async token =>
            {
                // A fresh scope per poll, not one held across the wait: a DbContext kept open for a
                // minute would hold a pooled connection for the whole wait, and every replica doing
                // that during a slow migration is how a deploy exhausts the pool it is waiting on.
                await using var scope = services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<SchemaVersionCheck>()
                    .InspectAsync(token);
            },
            options,
            logger,
            cancellationToken);
    }
}
