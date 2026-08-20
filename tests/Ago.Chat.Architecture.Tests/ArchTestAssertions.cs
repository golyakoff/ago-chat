using NetArchTest.Rules;

namespace Ago.Chat.Architecture.Tests;

internal static class ArchTestAssertions
{
    public static void ShouldPass(this TestResult result, string context)
    {
        var offenders = result.FailingTypeNames is null
            ? "no detail available"
            : string.Join(", ", result.FailingTypeNames);

        Assert.True(result.IsSuccessful, $"{context}. Offenders: {offenders}");
    }
}
