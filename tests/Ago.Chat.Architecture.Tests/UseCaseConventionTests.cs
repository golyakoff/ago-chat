using NetArchTest.Rules;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// clean-architecture.md: a handler orchestrates and nothing more, so it is sealed (no subclass
/// hook to smuggle extra behaviour in through) and lives under <c>UseCases/</c>, where a reviewer
/// sees a whole feature without navigating elsewhere.
/// </summary>
public class UseCaseConventionTests
{
    [Fact]
    public void Handlers_AreSealed() =>
        Types.InAssembly(TestAssemblies.Application.Reflection)
            .That().HaveNameEndingWith("Handler")
            .Should().BeSealed()
            .GetResult()
            .ShouldPass("every *Handler in Ago.Chat.Application must be sealed");

    [Fact]
    public void Handlers_LiveUnderUseCases() =>
        Types.InAssembly(TestAssemblies.Application.Reflection)
            .That().HaveNameEndingWith("Handler")
            .Should().ResideInNamespaceContaining("UseCases")
            .GetResult()
            .ShouldPass("every *Handler in Ago.Chat.Application must live under UseCases/");
}
