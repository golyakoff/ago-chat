namespace Ago.Chat.Architecture.Tests;

/// <summary>What counts as "the BCL" for the Domain allowlist test - the set of assembly names the
/// compiler links against by default, none of which represent a real external dependency.</summary>
internal static class BclAssemblyNames
{
    public static bool IsBcl(string assemblyName) =>
        assemblyName is "System.Private.CoreLib" or "System.Runtime" or "netstandard" or "mscorlib"
        || assemblyName.StartsWith("System.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.CSharp", StringComparison.Ordinal);
}
