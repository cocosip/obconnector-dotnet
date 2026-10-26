using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private bool _readingClientCharacterSet;
        // Generated ASCII XA/maintenance controls must not gain an intervening ordinary SQL query.
        internal bool SkipClientCharacterSetVerification { get; set; }
        internal Encoding ClientEncoding => _session.ClientEncoding;

        internal async Task InitializeClientCharacterSet(bool asynchronous, CancellationToken token)
        {
            SkipClientCharacterSetVerification = true;
            foreach (var sql in new[] { "SET NAMES " + _session.ClientCharacterSet.Name, "SET character_set_results=utf8mb4" })
            {
                var request = OceanBaseCommand.SqlPacket(sql, 3);
                if (asynchronous) await SendAsync(request, executesSql: false).ConfigureAwait(false); else Send(request, executesSql: false);
                var packet = asynchronous ? await ReceiveAsync(token).ConfigureAwait(false) : Receive();
                var ok = OkPacket.Parse(packet, Capabilities);
                if (ok.AffectedRows != 0 || ok.LastInsertId != 0 || (ok.StatusFlags & ServerStatusFlags.MoreResultsExists) != 0)
                    throw new ProtocolException("Invalid client character set initialization response.");
                ObserveSessionState(ok);
                ResponseComplete();
            }
            SkipClientCharacterSetVerification = false;
            _session.SetClientCharacterSetVerified(false);
            await VerifyClientCharacterSet(3, asynchronous, token).ConfigureAwait(false);
        }

        private async Task VerifyClientCharacterSet(byte command, bool asynchronous, CancellationToken token)
        {
            if (_session.ClientCharacterSetVerified || _readingClientCharacterSet || SkipClientCharacterSetVerification || !_session.ClientCharacterSet.Managed ||
                command != 2 && command != 3 && command != 22 && command != 23 && command != 0xa1) return;
            if (_longDataPending || _responsePending)
            {
                _session.Break();
                throw new InvalidOperationException("Client character sets cannot be verified with staged parameter data or an unread response.");
            }
            _readingClientCharacterSet = true;
            try
            {
                var request = OceanBaseCommand.SqlPacket(HandshakeFlow.ClientCharacterSetSql, 3);
                if (asynchronous) await SendAsync(request, executesSql: false, readerToken: token).ConfigureAwait(false); else Send(request, executesSql: false);
                var result = new QueryResult(this, completeExchange: false);
                if (asynchronous) await result.StartAsync(token).ConfigureAwait(false); else result.Start();
                var hasRow = asynchronous ? await result.ReadAsync(token).ConfigureAwait(false) : result.Read();
                if (result.Columns.Length != 4 || !hasRow) throw new ProtocolException("Client character set verification requires four columns and one row.");
                var values = new string[4];
                for (var i = 0; i < values.Length; i++)
                {
                    var type = result.Columns[i].TypeCode;
                    if (type != 15 && type != 253 && type != 254) throw new ProtocolException("Invalid client character set state column.");
                    values[i] = HandshakeFlow.ReadClientCharacterSetValue(result.Row![i]);
                }
                if (asynchronous ? await result.ReadAsync(token).ConfigureAwait(false) : result.Read()) throw new ProtocolException("Extra client character set state row.");
                HandshakeFlow.ValidateClientCharacterSet(_session.ClientCharacterSet, values);
                _session.SetClientCharacterSetVerified(true);
            }
            catch { _session.Break(); throw; }
            finally { _readingClientCharacterSet = false; }
        }
    }
}
