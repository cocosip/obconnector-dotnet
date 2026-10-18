using System;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OceanBaseConnector.ConsumerTests
{
    // Compiled against each modern reference pack; xUnit remains a net10.0 host.
    public static class BatchRuntimeConsumerSample
    {
        public static async Task Main(string[] args)
        {
            var major = int.Parse(args[0]); Check(Environment.Version.Major == major, "Runtime");
            Check(typeof(ILoggerFactory).Assembly.GetName().Version!.Major == major, "Logging dependency");
            Check(typeof(ActivitySource).Assembly.GetName().Version!.Major == major, "DiagnosticSource dependency");
            using var parent = new Activity("application").Start(); var spans = new List<Activity>(); var measurements = 0;
            using var activityListener = new ActivityListener { ShouldListenTo = source => source.Name == "OceanBaseConnector", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData, SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData, ActivityStopped = activity => spans.Add(activity) };
            ActivitySource.AddActivityListener(activityListener);
            using var meterListener = new MeterListener { MeasurementsCompleted = (_, _) => { } }; meterListener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "OceanBaseConnector") owner.EnableMeasurementEvents(instrument); };
            meterListener.SetMeasurementEventCallback<long>((_, value, tags, _) => { Check(value == 1 && tags.Length == 4, "Operation metric"); measurements++; });
            meterListener.SetMeasurementEventCallback<double>((_, value, _, _) => Check(value >= 0, "Duration metric")); meterListener.Start();
            using var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = tokenSource.Token;
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(args[1]));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var report = new byte[] { 0, 0, 0, 6, 64, 0, 0, 0, 19, 0, 17, 9, 116, 105, 109, 101, 95, 122, 111, 110, 101, 6, 43, 48, 48, 58, 48, 48 };
            var serving = Task.Run(async () =>
            {
                for (var connectionIndex = 0; connectionIndex < 2; connectionIndex++)
                {
                    using var peer = await listener.AcceptTcpClientAsync(token); var stream = peer.GetStream();
                    await Send(stream, greeting, 0, token); await Receive(stream, token); await Send(stream, report, 2, token);
                    var count = connectionIndex == 0 ? 4 : 2;
                    for (var i = 0; i < count; i++)
                    {
                        var request = await Receive(stream, token); Check(request[0] == 3, "COM_QUERY");
                        Check(Encoding.UTF8.GetString(request, 1, request.Length - 1) == (i == 3 ? "bad" : i % 2 == 0 ? "one" : "two"), "Sequential SQL");
                        var ok = (byte[])report.Clone(); ok[1] = (byte)(i % 2 + 2);
                        await Send(stream, i == 3 ? new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48 } : ok, 1, token);
                    }
                    Check(await stream.ReadAsync(new byte[1], token) == 0, "Owned connection closed");
                }
            }, token);
            var configuration = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;Password=secret;TLS Mode=Disabled";
            using (var connection = new OceanBaseConnection(configuration))
            {
                await connection.OpenAsync(token); using var batch = connection.CreateBatch();
                var one = Add(batch, "one"); var two = Add(batch, "two");
                Check(await batch.ExecuteNonQueryAsync(token) == 5 && one.RecordsAffected == 2 && two.RecordsAffected == 3, "Counts");
                two.CommandText = "bad";
                try { await batch.ExecuteNonQueryAsync(token); throw new InvalidOperationException("Missing error"); }
                catch (OceanBaseException error) { Check(error.BatchCommand == two && error.ServerErrorCode == 1064 && !error.IsExecutionOutcomeUnknown && one.RecordsAffected == 2, "Failure attribution"); }
                connection.Close();
            }
            await using (var source = new OceanBaseDataSourceBuilder(configuration).Build())
            {
                Check(!source.ConnectionString.Contains("secret"), "Sanitized source");
                await using var batch = source.CreateBatch(); var one = Add(batch, "one"); var two = Add(batch, "two");
                await using (var reader = await batch.ExecuteReaderAsync(token))
                {
                    Check(reader.FieldCount == 0 && await reader.NextResultAsync(token) && !await reader.NextResultAsync(token), "Reader sequencing");
                    Check(reader.RecordsAffected == 5 && one.RecordsAffected == 2 && two.RecordsAffected == 3, "Reader counts");
                }
            }
            await serving.WaitAsync(token);
            Check(spans.Count == 8 && measurements == 8, "Diagnostic lifecycle counts");
            Check(spans.All(span => span.ParentId == parent.Id && !string.Join(";", span.TagObjects).Contains("secret")), "Parent and redaction");
            Check(spans.Count(span => span.Status == ActivityStatusCode.Error) == 1, "Known failure span");
            Check(Activity.Current == parent, "Ambient span restored");
            Console.WriteLine($"PASS batch and diagnostics {RuntimeInformation.FrameworkDescription} Logging={typeof(ILoggerFactory).Assembly.GetName().Version}");
        }
        private static DbBatchCommand Add(DbBatch batch, string sql)
        { var command = OceanBaseProviderFactory.Instance.CreateBatchCommand(); command.CommandText = sql; batch.BatchCommands.Add(command); return command; }
        private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
        private static async Task Send(Stream stream, byte[] payload, byte sequence, CancellationToken token)
        { await stream.WriteAsync(new byte[] { (byte)payload.Length, 0, 0, sequence }, token); await stream.WriteAsync(payload, token); }
        private static async Task<byte[]> Receive(Stream stream, CancellationToken token)
        {
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            var length = header[0] | header[1] << 8 | header[2] << 16; Check(length < 1_048_576, "Packet limit");
            var payload = new byte[length]; await stream.ReadExactlyAsync(payload, token); return payload;
        }
    }
}
