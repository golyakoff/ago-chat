namespace Ago.Chat.Application.UseCases.ListSuspensionsForOwner;

/// <summary>`22-08`: no parameters at all - "a console screen listing currently-suspended accounts"
/// (`docs/backlog/22-08-*.md`'s own Scope) spans every tenant, the identical "the resource is all
/// sites" shape <c>ListSitesForOwner</c> already has for its own unrestricted list.</summary>
public sealed record ListSuspensionsForOwner;
