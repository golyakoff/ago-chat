using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`26-316`: a deterministic double for <see cref="IModuleCredentialGenerator"/> - returns a
/// fixed, <see cref="Domain.ModuleCredential"/>-valid value (comfortably past its 16-char floor) so a
/// test that enables a module through <c>EnableModuleForSiteHandler</c> can assert on the exact credential
/// the registration gateway received. A counter suffix keeps repeated calls distinct without making the
/// value unpredictable, the same "scripted, not random" shape every other generator fake here uses.</summary>
public sealed class FakeModuleCredentialGenerator : IModuleCredentialGenerator
{
    public const string Prefix = "fake-generated-module-credential-";

    private int _count;

    public string NewCredential() => $"{Prefix}{++_count:D4}";

    public string Last => $"{Prefix}{_count:D4}";
}
