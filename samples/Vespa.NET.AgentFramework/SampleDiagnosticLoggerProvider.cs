using Microsoft.Extensions.Logging;

namespace Vespa.AgentFrameworkSample;

/// <summary>Shows provider failures without printing conversation content or exception messages.</summary>
internal sealed class SampleDiagnosticLoggerProvider(TextWriter? writer = null) : ILoggerProvider
{
    private readonly TextWriter _writer = writer ?? Console.Error;
    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(categoryName, _writer);
    public void Dispose() { }

    private sealed class DiagnosticLogger(string category, TextWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                writer.WriteLine($"{logLevel}: {category}; event={eventId.Id}; failure={exception?.GetType().Name ?? "unspecified"}. Memory persistence or retrieval may be incomplete.");
        }
    }
}
