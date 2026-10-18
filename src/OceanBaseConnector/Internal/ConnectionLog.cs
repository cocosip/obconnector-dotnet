using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OceanBaseConnector.Internal
{
    internal sealed class ConnectionLog
    {
        private readonly ILogger _logger;

        internal ConnectionLog(ILoggerFactory? factory)
        {
            try { _logger = (factory ?? NullLoggerFactory.Instance).CreateLogger("OceanBaseConnector.Connection"); }
            catch { _logger = NullLogger.Instance; }
        }

        internal void Opening(string endpoint) => Write(1000, "Opening", endpoint, null, 0, null);
        internal void Opened(string endpoint, OceanBaseCompatibilityMode mode, long elapsed) => Write(1001, "Opened", endpoint, mode, elapsed, null);
        internal void Failed(string endpoint, long elapsed, Exception error) => Write(1002, "OpenFailed", endpoint, null, elapsed, error);
        internal void Closed(string endpoint) => Write(1003, "Closed", endpoint, null, 0, null);

        private void Write(int id, string name, string endpoint, OceanBaseCompatibilityMode? mode, long elapsed, Exception? error)
        {
            // An application log provider must never change an operation's outcome or cleanup.
            try
            {
                var level = error == null ? LogLevel.Debug : LogLevel.Warning;
                if (!_logger.IsEnabled(level)) return;
                var databaseError = error as OceanBaseException;
                _logger.Log(level, new EventId(id, name),
                    "{Operation} Endpoint={Endpoint} Mode={Mode} ElapsedMilliseconds={ElapsedMilliseconds} ErrorCode={ErrorCode} SqlState={SqlState} Canceled={Canceled}",
                    name, endpoint, mode, elapsed, databaseError?.ServerErrorCode, databaseError?.SqlState,
                    error is OperationCanceledException);
            }
            catch { }
        }
    }
}
