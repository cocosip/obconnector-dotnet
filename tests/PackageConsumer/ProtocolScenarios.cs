using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector;

internal static class ProtocolScenarios
{
    public static async Task Verify()
    {
        foreach (var oracle in new[] { false, true })
        foreach (var asynchronous in new[] { false, true })
        foreach (var sequential in new[] { false, true })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var stopListener = stop.Token.Register(listener.Stop);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var serving = Task.Run(() => Serve(listener, oracle, stop.Token));
                var consuming = Task.Run(() => Consume(port, oracle, asynchronous, sequential, stop.Token));
                var completion = Task.WhenAll(serving, consuming);
                var first = await Task.WhenAny(serving, consuming).ConfigureAwait(false);
                if (first.IsFaulted || first.IsCanceled) stop.Cancel();
                try { await completion.ConfigureAwait(false); }
                catch { throw (Exception?)completion.Exception ?? new InvalidOperationException("Package protocol canceled"); }
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Package protocol: " + (oracle ? "Oracle" : "MySQL") +
                    "/" + (asynchronous ? "async" : "sync") + "/" + (sequential ? "sequential" : "buffered"), error);
            }
            finally { listener.Stop(); }
        }
    }

    private static async Task Consume(int port, bool oracle, bool asynchronous, bool sequential, CancellationToken token)
    {
        using var connection = new OceanBaseConnection("Server=127.0.0.1;Port=" + port +
            ";User ID=synthetic;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Connection Timeout=5;Command Timeout=5;ExpectedMode=" +
            (oracle ? "Oracle" : "MySql"));
        try
        {
            if (asynchronous) await connection.OpenAsync(token).ConfigureAwait(false); else connection.Open();
            Check(connection.CompatibilityMode == (oracle ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql), "Authenticated mode");
            using (var command = connection.CreateCommand())
            {
                command.CommandText = oracle ? "select :p" : "select ?";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", DbType = DbType.Int32, Value = 7 });
                var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
                using var reader = asynchronous
                    ? await command.ExecuteReaderAsync(behavior, token).ConfigureAwait(false) : command.ExecuteReader(behavior);
                Check(reader.FieldCount == 1 && reader.GetName(0) == "n", "Binary result metadata");
                Check(asynchronous ? await reader.ReadAsync(token).ConfigureAwait(false) : reader.Read(), "First binary row");
                Check(Convert.ToDecimal(reader.GetValue(0)) == 42m, "Bound binary value");
                Check(asynchronous ? await reader.ReadAsync(token).ConfigureAwait(false) : reader.Read(), "Second binary row");
                Check(reader.IsDBNull(0), "Binary SQL NULL");
                Check(!(asynchronous ? await reader.ReadAsync(token).ConfigureAwait(false) : reader.Read()), "Binary terminal row");
                Check(!(asynchronous ? await reader.NextResultAsync(token).ConfigureAwait(false) : reader.NextResult()), "Binary terminal result");
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select rejected";
                try
                {
                    if (asynchronous) await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); else command.ExecuteNonQuery();
                    throw new InvalidOperationException("Expected server error");
                }
                catch (OceanBaseException error)
                {
                    Check(error.ServerErrorCode == 1064 && error.SqlState == "42000" && !error.IsExecutionOutcomeUnknown,
                        "Known terminal server error");
                }
            }
            Check(connection.State == ConnectionState.Open, "Server ERR preserves connection");
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select recovered";
                Check((asynchronous ? await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) : command.ExecuteNonQuery()) == 1,
                    "Command after server ERR");
            }
            using (var transaction =
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
                asynchronous ? await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token).ConfigureAwait(false) :
#endif
                connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                using var command = connection.CreateCommand();
                command.Transaction = (OceanBaseTransaction)transaction;
                command.CommandText = "update package_probe set v=1";
                Check((asynchronous ? await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) : command.ExecuteNonQuery()) == 1,
                    "Transaction command");
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
                if (asynchronous) await transaction.CommitAsync(token).ConfigureAwait(false); else
#endif
                transaction.Commit();
            }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
            if (asynchronous) await connection.CloseAsync().ConfigureAwait(false); else
#endif
            connection.Close();
            Check(connection.State == ConnectionState.Closed, "Returned logical connection");
            using var borrower = new OceanBaseConnection(connection.ConnectionString);
            try
            {
                if (asynchronous) await borrower.OpenAsync(token).ConfigureAwait(false); else borrower.Open();
                using var command = borrower.CreateCommand(); command.CommandText = "select reborrowed";
                Check((asynchronous ? await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) : command.ExecuteNonQuery()) == 2,
                    "Reset session reused by another logical connection");
            }
            finally { OceanBaseConnection.ClearPool(borrower); }
        }
        finally { OceanBaseConnection.ClearPool(connection); }
    }

    private static void Serve(TcpListener listener, bool oracle, CancellationToken token)
    {
        using var peer = listener.AcceptTcpClient();
        using var stopPeer = token.Register(peer.Close);
        peer.NoDelay = true;
        var stream = peer.GetStream(); stream.ReadTimeout = 5000; stream.WriteTimeout = 5000;
        // Same fully synthetic greeting as tests/fixtures/protocol/initial-handshake-oceanbase.json.
        Send(stream, Convert.FromBase64String("CjUuNy40LU9jZWFuQmFzZSA0LjIuMQDSBAAAAQIDBAUGBwgAAIotAgCoABUAAAAAAAAAAAAAERITFBUWFxgZGhscAG15c3FsX25hdGl2ZV9wYXNzd29yZAA="), 0);
        var authentication = Receive(stream, 1);
        Check(authentication.Length >= 32 && authentication[8] == 45, "Authentication response");
        var status = (byte)(oracle ? 6 : 2);
        Send(stream, SessionReport(status), 2);
        Query(stream, oracle ? "select ob_version(), @@ob_compatibility_mode from dual" :
            "select ob_version(), @@ob_compatibility_mode, @@autocommit from dual");
        ReplyText(stream, status, oracle ? new[] { "4.4.2.2", "ORACLE" } : new[] { "4.4.2.2", "MYSQL", "1" });
        if (oracle) { Query(stream, "set autocommit = 1"); Send(stream, SessionReport(status), 1); }

        Sql(stream, 22, "select ?");
        Send(stream, new byte[] { 0, 55, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0 }, 1);
        Send(stream, Column(3), 2); Send(stream, Eof(status), 3);
        Send(stream, Column(3), 4); Send(stream, Eof(status), 5);
        Equal(Receive(stream, 0), new byte[] { 23, 55, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 3, 0, 7, 0, 0, 0 }, "Bound integer execute");
        Send(stream, new byte[] { 1 }, 1); Send(stream, Column(3), 2); Send(stream, Eof(status), 3);
        Send(stream, new byte[] { 0, 0, 42, 0, 0, 0 }, 4);
        Send(stream, new byte[] { 0, 4 }, 5); Send(stream, Eof(status), 6);
        Equal(Receive(stream, 0), new byte[] { 25, 55, 0, 0, 0 }, "Prepared close");
        if (oracle) Discovery(stream, status);
        Query(stream, "select rejected");
        Send(stream, new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48, 115, 121, 110, 116, 104, 101, 116, 105, 99 }, 1);
        if (oracle) Discovery(stream, status);
        Query(stream, "select recovered"); Send(stream, SessionReport(status, 1), 1);
        Query(stream, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"); Send(stream, SessionReport(status), 1);
        Query(stream, "set autocommit = 0"); Send(stream, SessionReport(0), 1);
        Query(stream, "update package_probe set v=1"); Send(stream, SessionReport(1, 1), 1);
        Query(stream, "COMMIT"); Send(stream, SessionReport(0), 1);
        Query(stream, "set autocommit = 1"); Send(stream, SessionReport(status), 1);
        Equal(Receive(stream, 0), new byte[] { 31 }, "Pool reset"); Send(stream, SessionReport(status), 1);
        if (oracle) { Query(stream, "set autocommit = 1"); Send(stream, SessionReport(status), 1); }
        Equal(Receive(stream, 0), new byte[] { 14 }, "Reborrow ping"); Send(stream, SessionReport(status), 1);
        Query(stream, "select reborrowed"); Send(stream, SessionReport(status, 2), 1);
        Check(stream.ReadByte() == -1, "Pool clear closes physical connection");
    }

    private static void Discovery(Stream stream, byte status)
    {
        Query(stream, "SELECT SESSIONTIMEZONE FROM DUAL"); ReplyText(stream, status, "+00:00");
        Query(stream, "SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL");
        ReplyText(stream, status, "utf8mb4", "utf8mb4", "utf8mb4");
    }

    private static void ReplyText(Stream stream, byte status, params string[] values)
    {
        Send(stream, new[] { (byte)values.Length }, 1);
        for (var i = 0; i < values.Length; i++) Send(stream, Column(253), (byte)(2 + i));
        Send(stream, Eof(status), (byte)(2 + values.Length));
        using var row = new MemoryStream();
        foreach (var value in values) Text(row, value);
        Send(stream, row.ToArray(), (byte)(3 + values.Length));
        Send(stream, Eof(status), (byte)(4 + values.Length));
    }

    private static byte[] SessionReport(byte status, byte affected = 0)
    {
        using var state = new MemoryStream();
        var names = new[] { "time_zone", "ncharacter_set_connection", "character_set_results", "character_set_connection" };
        var values = new[] { "+00:00", "utf8mb4", "utf8mb4", "utf8mb4" };
        for (var i = 0; i < names.Length; i++)
        {
            state.WriteByte(0); state.WriteByte((byte)(2 + names[i].Length + values[i].Length));
            Text(state, names[i]); Text(state, values[i]);
        }
        using var packet = new MemoryStream();
        Write(packet, new byte[] { 0, affected, 0, status, 64, 0, 0, 0 });
        packet.WriteByte((byte)state.Length); Write(packet, state.ToArray()); return packet.ToArray();
    }

    private static byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110,
        12, 45, 0, 11, 0, 0, 0, type, 0, 0, 0, 0, 0 };
    private static byte[] Eof(byte status) => new byte[] { 254, 0, 0, status, 0 };
    private static void Query(Stream stream, string sql) => Sql(stream, 3, sql);
    private static void Sql(Stream stream, byte command, string sql)
    {
        var payload = Receive(stream, 0);
        Check(payload.Length > 0 && payload[0] == command && Encoding.UTF8.GetString(payload, 1, payload.Length - 1) == sql,
            "Expected command " + command + ": " + sql + "; actual=" + BitConverter.ToString(payload));
    }
    private static void Text(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Check(bytes.Length < 251, "Synthetic text length"); stream.WriteByte((byte)bytes.Length); Write(stream, bytes);
    }
    private static void Send(Stream stream, byte[] payload, byte sequence)
    {
        Write(stream, new byte[] { (byte)payload.Length, (byte)(payload.Length >> 8), (byte)(payload.Length >> 16), sequence });
        Write(stream, payload);
    }
    private static byte[] Receive(Stream stream, byte sequence)
    {
        var header = Read(stream, 4);
        Check(header[3] == sequence, "Packet sequence");
        var length = header[0] | header[1] << 8 | header[2] << 16;
        Check(length > 0 && length <= 1_048_576, "Synthetic packet bound"); return Read(stream, length);
    }
    private static byte[] Read(Stream stream, int length)
    {
        var bytes = new byte[length];
        for (var offset = 0; offset < length;)
        {
            var read = stream.Read(bytes, offset, length - offset);
            if (read == 0) throw new EndOfStreamException("Incomplete package protocol scenario");
            offset += read;
        }
        return bytes;
    }
    private static void Write(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    private static void Equal(byte[] actual, byte[] expected, string contract)
    {
        Check(actual.Length == expected.Length, contract + " length");
        for (var i = 0; i < actual.Length; i++) Check(actual[i] == expected[i], contract + " byte " + i);
    }
    private static void Check(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException("Package protocol failed: " + contract);
    }
}
