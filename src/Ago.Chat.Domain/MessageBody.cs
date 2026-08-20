namespace Ago.Chat.Domain;

/// <summary>
/// A non-empty message body, bounded so one message cannot become an unbounded write.
/// </summary>
public readonly record struct MessageBody
{
    // No product requirement pins this yet - generous enough for a real support message, small
    // enough that one row is never the reason an insert is slow. Revisit if a real limit surfaces.
    public const int MaxLength = 8000;

    public string Value { get; }

    public MessageBody(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Message body cannot be empty.", nameof(value));
        }

        if (value.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Message body cannot exceed {MaxLength} characters.", nameof(value));
        }

        Value = value;
    }
}
