using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OceanBaseConnector.ConsumerTests
{
    // Also compiled directly against an older reference pack for Windows runtime acceptance.
    public static class RuntimeConsumerSample
    {
        public static async Task Main(string[] args)
        {
            Check(Environment.Version.Major == int.Parse(args[0]), "Selected runtime");
            Check(typeof(ILoggerFactory).Assembly.GetName().Version!.Major == int.Parse(args[1]), "Logging dependency major");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(args[2]));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(cancellation.Token);
                var stream = peer.GetStream();
                await Send(stream, greeting, 0, cancellation.Token);
                await Receive(stream, cancellation.Token);
                await Send(stream, new byte[] { 0, 0, 0, 6, 0, 0, 0 }, 2, cancellation.Token);
                Check((await Receive(stream, cancellation.Token))[0] == 3, "Public query");
                await Send(stream, new byte[] { 1 }, 1, cancellation.Token);
                await Send(stream, new byte[] { 3, 100, 101, 102, 1, 116, 0, 0, 1, 110, 1, 110,
                    12, 45, 0, 8, 0, 0, 0, 246, 0, 0, 2, 6, 0 }, 2, cancellation.Token);
                await Send(stream, new byte[] { 254, 0, 0, 6, 0 }, 3, cancellation.Token);
                await Send(stream, new byte[] { 5, 49, 50, 46, 51, 52 }, 4, cancellation.Token);
                await Send(stream, new byte[] { 254, 0, 0, 6, 0 }, 5, cancellation.Token);
            }, cancellation.Token);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled", NullLoggerFactory.Instance);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await connection.OpenAsync(canceled.Token); throw new InvalidOperationException("Pre-cancel ignored"); }
            catch (OperationCanceledException error) { Check(error.CancellationToken == canceled.Token, "Caller token"); }
            Check(connection.State == ConnectionState.Closed, "Pre-cancel state");
            await connection.OpenAsync(cancellation.Token);
            Check(connection.CompatibilityMode == OceanBaseCompatibilityMode.Oracle, "Authenticated mode");
            using var command = connection.CreateCommand(); command.CommandText = "select n";
            using (var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellation.Token))
            {
                var schema = reader.GetColumnSchema();
                Check(schema.Count == 1 && schema[0].DataType == typeof(decimal), "Declared decimal type");
                Check((int)schema[0]["ProviderType"]! == (int)OceanBaseDbType.Decimal, "Column provider identity");
                Check((int)reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.ProviderType] == (int)OceanBaseDbType.Decimal, "Schema table identity");
                Check(await reader.ReadAsync(cancellation.Token) && reader.GetDecimal(0) == 12.34m, "Row value");
                Check(((OceanBaseDecimal)reader.GetProviderSpecificValue(0)).ToDecimal() == 12.34m, "Provider value");
                Check(!await reader.NextResultAsync(cancellation.Token), "Terminal response");
            }
            Check(connection.State == ConnectionState.Closed, "CloseConnection");
            var value = (OceanBaseParameter)command.CreateParameter(); value.Value = ulong.MaxValue; value.OceanBaseDbType = OceanBaseDbType.UInt64;
            Check(value.DbType == DbType.UInt64, "Parameter override");
            Check(new OceanBaseDecimal(12.3400m).ToDecimal() == 12.3400m, "Exact decimal");
            await serving.WaitAsync(cancellation.Token);
            Console.WriteLine($"PASS {args[3]} {RuntimeInformation.FrameworkDescription} Logging={typeof(ILoggerFactory).Assembly.GetName().Version}");
        }

        private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
        private static async Task Send(Stream stream, byte[] payload, byte sequence, CancellationToken token)
        {
            await stream.WriteAsync(new byte[] { (byte)payload.Length, 0, 0, sequence }, token);
            await stream.WriteAsync(payload, token);
        }
        private static async Task<byte[]> Receive(Stream stream, CancellationToken token)
        {
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            var length = header[0] | header[1] << 8 | header[2] << 16;
            Check(length < 1_048_576, "Sample packet bound");
            var payload = new byte[length]; await stream.ReadExactlyAsync(payload, token); return payload;
        }
    }
}
