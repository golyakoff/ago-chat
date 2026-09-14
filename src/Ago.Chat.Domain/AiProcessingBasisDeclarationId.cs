namespace Ago.Chat.Domain;

/// <summary>`25-04`: one row per declaration - see <see cref="AiProcessingBasisDeclaration"/> for why a
/// declaration is its own insert-only fact with its own id, rather than a column on
/// <see cref="AiAddOnEnablement"/>.</summary>
public readonly record struct AiProcessingBasisDeclarationId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
