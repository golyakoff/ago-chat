using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Settable in a test, never ambient - the point of taking <see cref="IClock"/> as a
/// dependency at all (date-and-time.md).</summary>
public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
