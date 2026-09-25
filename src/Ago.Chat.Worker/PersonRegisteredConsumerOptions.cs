namespace Ago.Chat.Worker;

public sealed class PersonRegisteredConsumerOptions
{
    public const string SectionName = "PersonRegisteredConsumer";

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
