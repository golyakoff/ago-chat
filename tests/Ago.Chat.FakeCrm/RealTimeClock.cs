using Ago.Platform.Kernel;

namespace Ago.Chat.FakeCrm;

/// <summary>
/// The one place this project reads the system clock (date-and-time.md's "time comes from IClock"
/// rule) - a tiny local implementation rather than pulling in Ago.Platform.Hosting's own SystemClock,
/// so this disposable test double does not take on that package's DI/health-check/OpenTelemetry
/// wiring conventions for the sake of one property it does not otherwise need.
/// </summary>
public sealed class RealTimeClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
