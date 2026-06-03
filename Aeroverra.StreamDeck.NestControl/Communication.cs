namespace Aeroverra.StreamDeck.NestControl
{
    internal static class Communication
    {
        internal static Task LogAsync(LogLevel level, string content) => Task.CompletedTask;

        internal static Task MetricsAsync() => Task.CompletedTask;
    }
}
