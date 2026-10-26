using System;
using System.Data.Common;

namespace OceanBaseConnector
{
    /// <summary>
    /// The exception the driver throws for database and protocol errors. It
    /// carries the server error code and SQLSTATE when the server supplied
    /// them, and <see cref="IsExecutionOutcomeUnknown"/> marks cases where a
    /// command was already on the wire but no trustworthy terminal response
    /// arrived (cancellation, timeout, network break), or a connection-level
    /// server error left execution uncertain: such SQL may or may
    /// not have executed and must never be retried automatically. Messages
    /// never contain SQL text, bind values or credentials.
    /// </summary>
    public sealed class OceanBaseException : DbException
    {
        internal OceanBaseException(
            string message,
            int serverErrorCode = 0,
            string? sqlState = null,
            bool isExecutionOutcomeUnknown = false,
            Exception? innerException = null,
            int? xaErrorCode = null)
            : base(message, innerException)
        {
            ServerErrorCode = serverErrorCode;
            _sqlState = sqlState;
            IsExecutionOutcomeUnknown = isExecutionOutcomeUnknown;
            XaErrorCode = xaErrorCode;
        }

        /// <summary>Server error code; 0 when the error is client-local.</summary>
        public int ServerErrorCode { get; }

        /// <summary>DBMS_XA function return code; null for errors without a trusted XA return value.</summary>
        public int? XaErrorCode { get; }

        /// <summary>Server SQLSTATE; null when not supplied.</summary>
#if NET8_0_OR_GREATER
        public override string? SqlState => _sqlState;
#else
        public string? SqlState => _sqlState;
#endif

        /// <summary>True when a sent command's outcome cannot be determined.</summary>
        public bool IsExecutionOutcomeUnknown { get; }
#if NET8_0_OR_GREATER
        /// <summary>Original commands in a failed rewritten statement, in order; null for unmerged execution.
        /// The collection is immutable; the command objects belong to the caller.</summary>
        public System.Collections.Generic.IReadOnlyList<DbBatchCommand>? BatchCommandGroup { get; internal set; }
        internal DbBatchCommand? FailedBatchCommand { get; set; }
        protected override DbBatchCommand? DbBatchCommand => FailedBatchCommand;
#endif

        private readonly string? _sqlState;
    }
}
