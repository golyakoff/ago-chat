namespace Ago.Chat.Architecture.Tests;

/// <summary>`20-07`: finds this checkout's own <c>src/</c> directory from the test assembly's build
/// output, by walking upward until a directory containing <c>Ago.Chat.slnx</c> is found - the marker
/// this whole solution is rooted at. Safe under CI's own "check out the full repository, run
/// <c>dotnet test</c> from within it" assumption; there is no other assumption this guard could make
/// without embedding an absolute path.</summary>
internal static class SourceTreeLocator
{
    public static string FindSrcDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ago.Chat.slnx")))
            {
                var src = Path.Combine(dir.FullName, "src");
                if (!Directory.Exists(src))
                {
                    throw new InvalidOperationException($"Found Ago.Chat.slnx at {dir.FullName} but no src/ beside it.");
                }

                return src;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate Ago.Chat.slnx by walking up from {AppContext.BaseDirectory}.");
    }
}
