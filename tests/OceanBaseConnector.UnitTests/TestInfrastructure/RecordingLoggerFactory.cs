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
        public string? EnabledCategory { get; set; }
        public int DisposeCount { get; private set; }
        public int CreateCount { get; private set; }
        public ILogger CreateLogger(string categoryName)
        {
            CreateCount++;
            if (ThrowOnCreate) throw new InvalidOperationException("logger creation failure");
            return EnabledCategory == null ? this : new CategoryLogger(this, categoryName);
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

        private sealed class CategoryLogger(RecordingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => owner.EnabledCategory == category && owner.IsEnabled(level);
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Log(level, eventId, state, exception, formatter);
        }
    }
}
