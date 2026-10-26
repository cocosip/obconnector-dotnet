using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        public override void ChangeDatabase(string databaseName)
        {
            var operation = StartDatabaseChange(databaseName, CancellationToken.None);
            using var exchange = operation.Exchange;
            exchange.StartDiagnostics("change_database");
            try
            {
                exchange.Send(operation.Request);
                CompleteDatabaseChange(exchange, databaseName, exchange.Receive());
            }
            catch (Exception error) { throw exchange.Fail(error); }
        }

#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override async Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            var operation = StartDatabaseChange(databaseName, cancellationToken);
            using var exchange = operation.Exchange;
            exchange.StartDiagnostics("change_database");
            try
            {
                await exchange.SendAsync(operation.Request).ConfigureAwait(false);
                CompleteDatabaseChange(exchange, databaseName, await exchange.ReceiveAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) { throw exchange.Fail(error); }
        }
#endif

        private (CommandExchange Exchange, byte[] Request) StartDatabaseChange(string database, CancellationToken token)
        {
            EnsureAmbientTransaction();
            token.ThrowIfCancellationRequested();
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (database.Length == 0 || database.IndexOf('\0') >= 0) throw new ArgumentException("A nonempty database name without NUL is required.", nameof(database));
            lock (_gate)
            {
                var session = GetOpenSession();
                if (session.ClientEncoding.GetByteCount(database) >= PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Database name exceeds the packet size limit.", nameof(database));
                var request = OceanBaseCommand.SqlPacket(database, 2, session.ClientEncoding);
                if (_xaResource != null) throw new InvalidOperationException("The connection is dedicated to XA until Close.");
                if (session.Info!.Mode != OceanBaseCompatibilityMode.MySql) throw new NotSupportedException("Database switching is supported only in MySQL mode.");
                ValidateCommandTransaction(session, _transaction);
                return (new CommandExchange(session, _log, _builder.CommandTimeout, token, () => { }), request);
            }
        }

        private static void CompleteDatabaseChange(CommandExchange exchange, string database, byte[] packet)
        {
            var ok = OkPacket.Parse(packet, exchange.Capabilities);
            if (ok.AffectedRows != 0 || ok.LastInsertId != 0 ||
                (ok.StatusFlags & (ServerStatusFlags.MoreResultsExists | ServerStatusFlags.PsOutParameters | ServerStatusFlags.CursorExists | ServerStatusFlags.LastRowSent)) != 0)
                throw new ProtocolException("Invalid database change completion.");
            exchange.ObserveSessionState(ok);
            exchange.SetDatabase(database);
            exchange.Complete();
        }
    }
}
