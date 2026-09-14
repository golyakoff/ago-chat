using System.Reflection;
using Ago.Platform.Kernel;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `25-98`: the independent source `ErrorExtensionsRetryAfterTests`'s own doc comment on `25-91`
/// names as missing - "the only sound source for 'every error code, and its intended status' is
/// independent of the switch under test, and this codebase does not currently have one". This is
/// that source's *enumeration* half: every code a `*Errors`-shaped factory method can actually
/// produce, read by reflection off <see cref="TestAssemblies.Application"/> rather than typed out by
/// hand (which is exactly how the six-code list `25-98`'s own item text started from stayed a floor
/// rather than a ceiling for as long as it did).
///
/// <para><b>Why reflection over the compiled assembly, not a source-text scan.</b> `ErrorCodeMappingTests`
/// already reads <c>ErrorExtensions.cs</c> as source text, because that file's own switch has no
/// compiled structure worth reflecting over (a `string switch` erases to IL a scanner would have to
/// re-parse anyway). The factory methods are the opposite case: they are ordinary public static
/// methods with a stable, reflectable shape, and invoking the real method is the only way to read the
/// real code a real caller gets back - a hand-copied list would drift from the source it claims to
/// describe the same way the switch's own gaps did.</para>
/// </summary>
internal static class ErrorCodeCatalog
{
    /// <summary>One row per public static `Error`-returning factory method on every `*Errors`-shaped
    /// class in <c>Ago.Chat.Application</c> (any namespace - `AiAddOnErrors` sits one level deeper
    /// than <c>ConversationErrors</c>, and a scan that only looked at the root namespace would have
    /// missed the six codes this item's own audit found there). Each method is invoked once, with a
    /// placeholder argument per parameter - the arguments only ever reach a message-formatting
    /// interpolation (every factory method's own shape, confirmed by reading every one of them), never
    /// a branch that could change which <c>Code</c> comes back.</summary>
    public static IReadOnlyList<ErrorCodeSite> FromApplicationAssembly() =>
        TestAssemblies.Application.Reflection
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: true, IsSealed: true } // `static class` in IL
                && t.Name.EndsWith("Errors", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.ReturnType == typeof(Error))
                .Select(m => new ErrorCodeSite(t.FullName ?? t.Name, m.Name, Invoke(t, m))))
            .OrderBy(s => s.Code, StringComparer.Ordinal)
            .ToList();

    private static string Invoke(Type type, MethodInfo method)
    {
        var arguments = method.GetParameters().Select(PlaceholderFor).ToArray();
        var error = (Error?)method.Invoke(null, arguments)
            ?? throw new InvalidOperationException($"{type.FullName}.{method.Name} returned a null Error.");
        return error.Code;
    }

    /// <summary>A value for every parameter type this codebase's own `*Errors` factory methods
    /// actually use, confirmed by grepping every one of them rather than guessed - throws on anything
    /// new rather than silently skipping the method, so a future factory method with a parameter type
    /// nobody taught this helper about fails loudly in this test instead of vanishing from the
    /// catalog.</summary>
    private static object PlaceholderFor(ParameterInfo parameter)
    {
        var t = parameter.ParameterType;
        if (t == typeof(string))
        {
            return "placeholder";
        }

        if (t == typeof(Guid))
        {
            return Guid.Empty;
        }

        if (t == typeof(int))
        {
            return 1;
        }

        if (t == typeof(long))
        {
            return 1L;
        }

        if (t == typeof(TimeSpan))
        {
            return TimeSpan.FromSeconds(1);
        }

        if (t == typeof(DateOnly))
        {
            return new DateOnly(2026, 1, 1);
        }

        if (t == typeof(bool))
        {
            return false;
        }

        if (t.IsEnum)
        {
            return Enum.GetValues(t).GetValue(0)!;
        }

        throw new InvalidOperationException(
            $"ErrorCodeCatalog has no placeholder value for parameter type '{t}' on "
            + $"{parameter.Member.DeclaringType?.FullName}.{parameter.Member.Name} - add one rather than let "
            + "this factory method silently drop out of the catalog.");
    }
}

/// <summary>One `*Errors` factory method and the code it produces.</summary>
internal sealed record ErrorCodeSite(string DeclaringType, string Method, string Code)
{
    public override string ToString() => $"{DeclaringType}.{Method} -> \"{Code}\"";
}
