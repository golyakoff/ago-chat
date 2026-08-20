using System.Reflection;
using NetArchTest.Rules;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// adr/0012: the platform never references a product. The package boundary already makes this true
/// by construction - <c>ago-platform</c>'s source tree has no access to <c>ago-chat</c>'s - but the
/// test states it anyway, so the guarantee is a checked fact rather than something that only holds
/// until someone points the dev override (<c>AgoPlatformDevOverride</c>) the wrong way.
/// </summary>
public class PlatformBoundaryTests
{
    [Fact]
    public void Kernel_NeverReferencesAnyAgoChatAssembly() =>
        AssertNoAgoChatDependency(TestAssemblies.PlatformKernel.Reflection, "Ago.Platform.Kernel");

    [Fact]
    public void Hosting_NeverReferencesAnyAgoChatAssembly() =>
        AssertNoAgoChatDependency(TestAssemblies.PlatformHosting.Reflection, "Ago.Platform.Hosting");

    private static void AssertNoAgoChatDependency(Assembly assembly, string assemblyName) =>
        Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOn("Ago.Chat")
            .GetResult()
            .ShouldPass($"{assemblyName} must never reference an Ago.Chat.* assembly");
}
