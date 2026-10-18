using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    public sealed class RecordingLoggerFactory : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<(EventId EventId, string Message, Exception? Error)> Entries { get; } = new();
        public Action<EventId>? OnLog { get; set; }
        public bool ThrowOnCreate { get; set; }
        public bool ThrowOnLog { get; set; }
        public bool ThrowOnIsEnabled { get; set; }
        public int DisposeCount { get; private set; }
        public int CreateCount { get; private set; }
        public ILogger CreateLogger(string categoryName)
        {
            CreateCount++;
            return ThrowOnCreate ? throw new InvalidOperationException("logger creation failure") : this;
        }
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() => DisposeCount++;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => ThrowOnIsEnabled ? throw new InvalidOperationException("logger filter failure") : true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnLog) throw new InvalidOperationException("logger write failure");
            Entries.Enqueue((eventId, formatter(state, exception), exception));
            OnLog?.Invoke(eventId);
        }
    }
}
