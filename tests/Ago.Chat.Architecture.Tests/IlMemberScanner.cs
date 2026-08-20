using Mono.Cecil;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// NetArchTest reasons about type-level dependencies (a class having a field, parameter or base
/// type of X); it has no notion of "this one static member was called." Banning
/// <c>Guid.NewGuid()</c> specifically - not the <see cref="Guid"/> type, which ids use everywhere -
/// means reading method bodies directly, with the same library (Mono.Cecil) NetArchTest itself is
/// built on.
/// </summary>
internal static class IlMemberScanner
{
    public static IReadOnlyList<string> FindCallers(AssemblyDefinition assembly, string declaringTypeFullName, string memberName)
    {
        var offenders = new List<string>();

        foreach (var type in assembly.MainModule.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference called
                        && called.DeclaringType.FullName == declaringTypeFullName
                        && called.Name == memberName)
                    {
                        offenders.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        return offenders;
    }
}
