using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        private OceanBaseXaResource? _xaResource;

        /// <summary>Dedicates the current session to explicit XA until Close.
        /// Business commands require an active branch; local transactions are then rejected.</summary>
        public OceanBaseXaResource GetXaResource()
        {
            RejectAmbientTransaction();
            lock (_gate)
            {
                var session = GetOpenSession();
                if (_transaction != null) throw new InvalidOperationException("A local transaction is already active.");
                if (session.Info!.Mode == OceanBaseCompatibilityMode.Oracle && !session.UseOraclePrepareExecute)
                    throw new NotSupportedException("Oracle DBMS_XA requires Use Oracle Prepare Execute=true.");
                if (_xaResource == null)
                {
                    session.MarkDedicatedToXa();
                    _xaResource = new OceanBaseXaResource(this, session, _builder.CommandTimeout);
                    _transactionEpoch++;
                }
                return _xaResource;
            }
        }

        private CommandExchange StartXa(OceanBaseXaResource resource, XaOperation operation, OceanBaseXid? xid, int mode, CancellationToken token)
        {
            RejectAmbientTransaction(); token.ThrowIfCancellationRequested();
            if (operation != XaOperation.Recover && xid == null) throw new ArgumentNullException(nameof(xid));
            if (operation == XaOperation.Start && (uint)mode > (uint)OceanBaseXaStartMode.Resume ||
                operation == XaOperation.End && (uint)mode > (uint)OceanBaseXaEndMode.Suspend)
                throw new ArgumentOutOfRangeException(nameof(mode));
            lock (_gate)
            {
                var session = GetOpenSession();
                if (!ReferenceEquals(resource, _xaResource) || !ReferenceEquals(session, resource.Session) || resource.InProgress)
                    throw new InvalidOperationException("The XA resource has ended or has an active operation.");
                var state = resource.State;
                var same = xid != null && xid.Equals(resource.Xid);
                bool valid;
                switch (operation)
                {
                    case XaOperation.Start:
                        valid = mode == (int)OceanBaseXaStartMode.Resume ? state == XaState.Suspended && same
                            : state == XaState.Idle || mode == (int)OceanBaseXaStartMode.Join && state == XaState.Ended && same;
                        break;
                    case XaOperation.End: valid = state == XaState.Active && same; break;
                    case XaOperation.Prepare: valid = state == XaState.Ended && same && !resource.RollbackOnly; break;
                    case XaOperation.Commit:
                        valid = mode == 1 ? state == XaState.Ended && same && !resource.RollbackOnly
                            : state == XaState.Idle || state == XaState.Prepared && same;
                        break;
                    case XaOperation.Rollback: valid = state == XaState.Idle || same && (state == XaState.Ended || state == XaState.Prepared); break;
                    default: valid = state == XaState.Idle; break;
                }
                if (!valid) throw new InvalidOperationException("The XA operation does not match the branch identity or phase.");
                var exchange = new CommandExchange(session, _log, resource.Timeout, token, () => { }) { IsXa = true };
                resource.InProgress = true;
                return exchange;
            }
        }

        internal async Task<OceanBaseXaVote> ExecuteXa(OceanBaseXaResource resource, XaOperation operation, OceanBaseXid xid, int mode, bool asynchronous, CancellationToken token)
        {
            using var exchange = StartXa(resource, operation, xid, mode, token);
            exchange.StartDiagnostics("xa." + operation.ToString().ToLowerInvariant());
            try
            {
                var vote = OceanBaseXaVote.Prepared;
                if (exchange.Mode == OceanBaseCompatibilityMode.Oracle)
                    vote = await ExecuteOracleXa(resource, exchange, operation, xid, mode, asynchronous).ConfigureAwait(false);
                else await ExecuteMySqlXa(exchange, operation, xid, mode, asynchronous).ConfigureAwait(false);
                lock (_gate)
                {
                    RequireCurrentXa(resource);
                    switch (operation)
                    {
                        case XaOperation.Start: resource.Xid = xid; resource.State = XaState.Active; break;
                        case XaOperation.End:
                            resource.State = mode == 2 ? XaState.Suspended : XaState.Ended;
                            resource.RollbackOnly |= mode == 1; break;
                        case XaOperation.Prepare when vote != OceanBaseXaVote.ReadOnly: resource.State = XaState.Prepared; break;
                        default: resource.State = XaState.Idle; resource.Xid = null; resource.RollbackOnly = false; break;
                    }
                    _transactionEpoch++;
                    exchange.Complete();
                }
                return vote;
            }
            catch (Exception error) { throw exchange.Fail(error); }
            finally { lock (_gate) resource.InProgress = false; }
        }

        private static async Task ExecuteMySqlXa(CommandExchange exchange, XaOperation operation, OceanBaseXid xid, int mode, bool asynchronous)
        {
            var branch = xid.GetBranchQualifier();
            var identity = "0x" + XaHex(xid.GetGlobalTransactionId()) +
                "," + (branch.Length == 0 ? "X''" : "0x" + XaHex(branch)) +
                ",0x" + unchecked((uint)xid.FormatId).ToString("x8", CultureInfo.InvariantCulture);
            string sql;
            switch (operation)
            {
                case XaOperation.Start: sql = "XA START " + identity + (mode == 1 ? " JOIN" : mode == 2 ? " RESUME" : ""); break;
                case XaOperation.End: sql = "XA END " + identity + (mode == 2 ? " SUSPEND" : ""); break;
                case XaOperation.Prepare: sql = "XA PREPARE " + identity; break;
                case XaOperation.Commit: sql = "XA COMMIT " + identity + (mode == 1 ? " ONE PHASE" : ""); break;
                default: sql = "XA ROLLBACK " + identity; break;
            }
            var request = OceanBaseCommand.SqlPacket(sql, 3);
            byte[] response;
            if (asynchronous)
            {
                await exchange.SendAsync(request).ConfigureAwait(false);
                response = await exchange.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else { exchange.Send(request); response = exchange.Receive(); }
            var ok = OkPacket.Parse(response, exchange.Capabilities);
            if (ok.AffectedRows != 0 || ok.LastInsertId != 0 ||
                (ok.StatusFlags & (ServerStatusFlags.MoreResultsExists | ServerStatusFlags.PsOutParameters | ServerStatusFlags.CursorExists | ServerStatusFlags.LastRowSent)) != 0)
                throw new ProtocolException("Invalid XA control completion.");
            exchange.ObserveSessionState(ok);
            // Validate the final OB20 boundary before publishing a new branch phase.
            exchange.ResponseComplete();
        }

        internal async Task<OceanBaseXid[]> RecoverXa(OceanBaseXaResource resource, bool asynchronous, CancellationToken token)
        {
            using var exchange = StartXa(resource, XaOperation.Recover, null, 0, token);
            exchange.StartDiagnostics("xa.recover");
            try
            {
                if (exchange.Mode == OceanBaseCompatibilityMode.Oracle)
                {
                    var recovered = await RecoverOracleXa(resource, exchange, asynchronous).ConfigureAwait(false);
                    lock (_gate) { RequireCurrentXa(resource); exchange.Complete(); }
                    return recovered;
                }
                var request = OceanBaseCommand.SqlPacket("XA RECOVER", 3);
                if (asynchronous) await exchange.SendAsync(request).ConfigureAwait(false); else exchange.Send(request);
                var result = new QueryResult(exchange, completeExchange: false);
                if (asynchronous) await result.StartAsync(CancellationToken.None).ConfigureAwait(false); else result.Start();
                var columns = result.Columns;
                if (columns.Length != 4 || !XaIntegerColumn(columns[0]) || !XaIntegerColumn(columns[1]) || !XaIntegerColumn(columns[2]) ||
                    columns[3].Charset != 63 || !(columns[3].TypeCode >= 249 && columns[3].TypeCode <= 254))
                    throw new ProtocolException("Invalid XA recovery metadata.");
                var snapshot = new List<OceanBaseXid>();
                while (asynchronous ? await result.ReadAsync(CancellationToken.None).ConfigureAwait(false) : result.Read())
                {
                    if (snapshot.Count == 65536) throw new ProtocolException("XA recovery branch limit exceeded.");
                    var row = result.Row!;
                    var format = XaUnsigned(row[0], uint.MaxValue - 1);
                    var global = (int)XaUnsigned(row[1], 64);
                    var branch = (int)XaUnsigned(row[2], 64);
                    var bytes = row[3];
                    if (global == 0 || bytes == null || bytes.Length != global + branch)
                        throw new ProtocolException("Invalid XA recovery identity lengths.");
                    var globalBytes = new byte[global]; var branchBytes = new byte[branch];
                    Buffer.BlockCopy(bytes, 0, globalBytes, 0, global);
                    Buffer.BlockCopy(bytes, global, branchBytes, 0, branch);
                    snapshot.Add(new OceanBaseXid(unchecked((int)format), globalBytes, branchBytes));
                }
                lock (_gate) { RequireCurrentXa(resource); exchange.Complete(); }
                return snapshot.ToArray();
            }
            catch (Exception error)
            {
                var mapped = exchange.Fail(error is ProtocolException
                    ? new OceanBaseException("Invalid XA recovery response.", innerException: error) : error);
                // A malformed identity is not a usable recovery snapshot, even after its terminal EOF.
                if (error is ProtocolException) resource.Session.Break();
                throw mapped;
            }
            finally { lock (_gate) resource.InProgress = false; }
        }

        private void RequireCurrentXa(OceanBaseXaResource resource)
        {
            if (!ReferenceEquals(resource, _xaResource) || !ReferenceEquals(resource.Session, _session) || resource.Session.State != SessionState.Busy)
                throw new InvalidOperationException("The XA physical session is no longer active.");
        }

        private static bool XaIntegerColumn(ResultColumn column) =>
            (column.TypeCode == 1 || column.TypeCode == 2 || column.TypeCode == 3 || column.TypeCode == 8 || column.TypeCode == 9) && column.Scale == 0;

        private static uint XaUnsigned(byte[]? bytes, uint maximum)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 10) throw new ProtocolException("Invalid XA recovery integer.");
            uint result = 0;
            foreach (var b in bytes)
            {
                if (b < '0' || b > '9' || result > (maximum - (uint)(b - '0')) / 10)
                    throw new ProtocolException("XA recovery integer is outside the supported identity domain.");
                result = result * 10 + (uint)(b - '0');
            }
            if (result > maximum) throw new ProtocolException("XA recovery integer is outside the supported identity domain.");
            return result;
        }

        private static string XaHex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++) { chars[i * 2] = digits[bytes[i] >> 4]; chars[i * 2 + 1] = digits[bytes[i] & 15]; }
            return new string(chars);
        }
    }
}
