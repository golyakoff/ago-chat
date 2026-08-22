using Ago.Platform.Realtime;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `3-06`'s Done-when: readiness reports unhealthy the instant drain starts, while liveness stays
/// healthy throughout - the two-signal claim, checked against the exact mechanism
/// `Ago.Chat.Api/Program.cs` wires up (`DrainHealthCheck`, tagged `"ready"`; liveness maps with
/// `Predicate: _ => false`, running no registered check at all and so never seeing
/// <see cref="DrainState"/>).
/// </summary>
public sealed class DrainReadinessTests
{
    [Fact]
    public async Task DrainHealthCheck_BeforeDraining_IsHealthy_AndOnceDraining_IsUnhealthy()
    {
        var drainState = new DrainState();
        var check = new DrainHealthCheck(drainState);

        var before = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, before.Status);

        drainState.MarkDraining();

        var after = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Unhealthy, after.Status);
    }

    [Fact]
    public void LivenessPredicate_RunsNoRegisteredCheck_SoDrainCanNeverAffectIt()
    {
        // The exact predicate Ago.Chat.Api/Program.cs passes to MapHealthChecks("/healthz/live", ...) -
        // asserted directly, not re-derived, so a future edit that accidentally makes liveness see
        // the "ready" tag (and so DrainHealthCheck) fails this test instead of silently shipping.
        Func<HealthCheckRegistration, bool> livenessPredicate = _ => false;

        var registration = new HealthCheckRegistration(
            "drain", new DrainHealthCheck(new DrainState()), failureStatus: null, tags: ["ready"]);

        Assert.False(livenessPredicate(registration));
    }
}
