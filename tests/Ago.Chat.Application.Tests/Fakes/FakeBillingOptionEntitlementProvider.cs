using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `23-85`/`adr/0159`: an in-memory stand-in for the real <c>ConfiguredBillingOptionEntitlementProvider</c>
/// (`Ago.Chat.Infrastructure.Modules`) - a deployment's own `BillingOptionEntitlements:*` configuration
/// section, without a real <c>IConfiguration</c> to bind. <see cref="Map"/> lets a test declare exactly
/// the option-to-module mapping it needs; an option nobody mapped returns <see langword="null"/> from
/// <see cref="TryGet"/>, the identical "an undeclared option grants nothing" behaviour the real
/// implementation has for a configuration key that was never set.
/// </summary>
public sealed class FakeBillingOptionEntitlementProvider : IBillingOptionEntitlementProvider
{
    private readonly Dictionary<BillingOptionKey, ModuleKey> _mapped = [];

    public void Map(BillingOptionKey optionKey, ModuleKey moduleKey) => _mapped[optionKey] = moduleKey;

    public ModuleKey? TryGet(BillingOptionKey optionKey) => _mapped.GetValueOrDefault(optionKey);
}
