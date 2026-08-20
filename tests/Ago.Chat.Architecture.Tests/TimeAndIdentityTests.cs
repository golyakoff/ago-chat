using NetArchTest.Rules;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// adr/0011: ordering never depends on a clock, and a rule you cannot control in a test is a rule
/// you cannot test - so time and identity are parameters everywhere except Infrastructure.
/// <see cref="DateTime"/> the type is banned outright (<see cref="DateTimeOffset"/> replaces it
/// everywhere); the ambient-reading members <c>DateTimeOffset.UtcNow</c>/<c>.Now</c> and
/// <c>Guid.NewGuid()</c> are banned specifically - <see cref="DateTimeOffset"/> and
/// <see cref="Guid"/> as types are fine everywhere, since ids and timestamps are ordinary
/// parameters (<c>Ago.Platform.Kernel.IClock</c>, <c>IIdGenerator</c>).
/// </summary>
public class TimeAndIdentityTests
{
    [Fact]
    public void DateTimeType_NeverAppearsOutsideInfrastructure()
    {
        foreach (var assembly in TestAssemblies.NonInfrastructure)
        {
            Types.InAssembly(assembly.Reflection)
                .Should()
                .NotHaveDependencyOn("System.DateTime")
                .GetResult()
                .ShouldPass($"{assembly.Name} must not reference System.DateTime - use DateTimeOffset");
        }
    }

    [Fact]
    public void DateTimeOffsetUtcNowAndNow_NeverCalledOutsideInfrastructure()
    {
        foreach (var assembly in TestAssemblies.NonInfrastructure)
        {
            var offenders = IlMemberScanner.FindCallers(assembly.Cecil, "System.DateTimeOffset", "get_UtcNow")
                .Concat(IlMemberScanner.FindCallers(assembly.Cecil, "System.DateTimeOffset", "get_Now"))
                .ToList();

            Assert.True(offenders.Count == 0,
                $"{assembly.Name} reads the ambient clock directly - take DateTimeOffset as a parameter instead. Callers: {string.Join(", ", offenders)}");
        }
    }

    [Fact]
    public void GuidNewGuid_NeverCalledOutsideInfrastructure()
    {
        foreach (var assembly in TestAssemblies.NonInfrastructure)
        {
            var offenders = IlMemberScanner.FindCallers(assembly.Cecil, "System.Guid", "NewGuid");

            Assert.True(offenders.Count == 0,
                $"{assembly.Name} calls Guid.NewGuid() directly - use IIdGenerator instead. Callers: {string.Join(", ", offenders)}");
        }
    }
}
