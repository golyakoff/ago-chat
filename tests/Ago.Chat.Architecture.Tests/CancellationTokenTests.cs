using System.Reflection;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// A public <see cref="Task"/>-returning method with no <see cref="CancellationToken"/> is a
/// method nobody can ever cancel. This is a method-signature question, not a type-dependency one,
/// so it is checked with plain reflection rather than NetArchTest.
/// </summary>
public class CancellationTokenTests
{
    [Fact]
    public void PublicTaskReturningMethods_AcceptACancellationToken()
    {
        var offenders = new List<string>();

        foreach (var assembly in TestAssemblies.AllProduct)
        {
            foreach (var type in assembly.Reflection.GetTypes().Where(t => t.IsPublic || t.IsNestedPublic))
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

                foreach (var method in methods)
                {
                    if (!ReturnsTask(method.ReturnType))
                    {
                        continue;
                    }

                    if (method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)))
                    {
                        continue;
                    }

                    offenders.Add($"{assembly.Name}: {type.FullName}.{method.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Public Task-returning methods with no CancellationToken parameter: {string.Join(", ", offenders)}");
    }

    private static bool ReturnsTask(Type returnType) =>
        returnType == typeof(Task)
        || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>));
}
