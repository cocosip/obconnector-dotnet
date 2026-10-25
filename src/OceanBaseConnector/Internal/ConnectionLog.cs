using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OceanBaseConnector.Internal
{
    internal sealed class ConnectionLog
    {
        private readonly ILogger _logger;
        private readonly ILogger _commandLogger;
        private readonly ILogger _transactionLogger;

        internal ConnectionLog(ILoggerFactory? factory)
        {
            _logger = Create(factory, "OceanBaseConnector.Connection");
            _commandLogger = Create(factory, "OceanBaseConnector.Command");
            _transactionLogger = Create(factory, "OceanBaseConnector.Transaction");
        }

        private static ILogger Create(ILoggerFactory? factory, string category)
        {
            try { return (factory ?? NullLoggerFactory.Instance).CreateLogger(category); }
            catch { return NullLogger.Instance; }
        }

        internal void Opening(string endpoint) => Write(1000, "Opening", endpoint, null, 0, null);
        internal void Opened(string endpoint, OceanBaseCompatibilityMode mode, long elapsed) => Write(1001, "Opened", endpoint, mode, elapsed, null);
        internal void Failed(string endpoint, long elapsed, Exception error) => Write(1002, "OpenFailed", endpoint, null, elapsed, error);
        internal void Closed(string endpoint) => Write(1003, "Closed", endpoint, null, 0, null);
        internal void CommandFinished(string endpoint, OceanBaseCompatibilityMode mode, long elapsed, Exception? error) =>
            Write(error == null ? 1100 : 1101, error == null ? "CommandCompleted" : "CommandFailed", endpoint, mode, elapsed, error, _commandLogger);
        internal void TransactionFinished(string operation, string endpoint, OceanBaseCompatibilityMode mode, long elapsed, Exception? error) =>
            Write(error == null ? 1200 : 1201, "Transaction" + operation + (error == null ? "Completed" : "Failed"), endpoint, mode, elapsed, error, _transactionLogger);

        internal void SlowCommand(string operation, string endpoint, OceanBaseCompatibilityMode mode, double elapsed, int threshold, Exception? error)
        {
            var previous = Activity.Current;
            try
            {
                if (!_commandLogger.IsEnabled(LogLevel.Warning)) return;
                _commandLogger.Log(LogLevel.Warning, new EventId(1102, "SlowCommand"),
                    "SlowCommand Operation={Operation} Endpoint={Endpoint} Mode={Mode} ElapsedMilliseconds={ElapsedMilliseconds} ThresholdMilliseconds={ThresholdMilliseconds} Outcome={Outcome} OutcomeUnknown={OutcomeUnknown}",
                    operation, endpoint, mode, elapsed, threshold, DriverDiagnostics.Outcome(error), (error as OceanBaseException)?.IsExecutionOutcomeUnknown ?? false);
            }
            catch { }
            finally { try { Activity.Current = previous; } catch { } }
        }

        private void Write(int id, string name, string endpoint, OceanBaseCompatibilityMode? mode, long elapsed, Exception? error, ILogger? logger = null)
        {
            // An application log provider must never change an operation's outcome or cleanup.
            try
            {
                var level = error == null ? LogLevel.Debug : LogLevel.Warning;
                logger ??= _logger;
                if (!logger.IsEnabled(level)) return;
                var databaseError = error as OceanBaseException;
                logger.Log(level, new EventId(id, name),
                    "{Operation} Endpoint={Endpoint} Mode={Mode} ElapsedMilliseconds={ElapsedMilliseconds} ErrorCode={ErrorCode} SqlState={SqlState} Canceled={Canceled} OutcomeUnknown={OutcomeUnknown}",
                    name, endpoint, mode, elapsed, databaseError?.ServerErrorCode, databaseError?.SqlState,
                    error is OperationCanceledException || databaseError?.InnerException is OperationCanceledException,
                    databaseError?.IsExecutionOutcomeUnknown ?? false);
            }
            catch { }
        }
    }
}
