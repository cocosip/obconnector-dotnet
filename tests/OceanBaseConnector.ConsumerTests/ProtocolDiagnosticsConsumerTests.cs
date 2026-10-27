using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    [Collection("Consumer diagnostics")]
    public sealed class ProtocolDiagnosticsConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var oracle in new[] { false, true })
                        foreach (var compressed in new[] { false, true })
                            foreach (var observer in new[] { "normal", "filter", "write", "source" })
                                yield return new object[] { target, asynchronous, oracle, compressed, observer };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetReportsOnlyMetadataAndSurvivesObserverFailures(string target, bool asynchronous, bool oracle, bool compressed, string observer)
        {
            InitializeFixtureDiagnostics(); using var capture = new Capture(observer);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            await using var server = new CompressionServer(Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!), oracle, authSwitch: true);
            var context = new AssemblyLoadContext("protocol-diagnostics-" + Guid.NewGuid(), isCollectible: true);
            DbConnection? connection = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={server.Port};User ID=u;Password=secret;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Use Compression={compressed}";
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                for (var i = 0; i < 2; i++)
                {
                    if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "select 'private-query'";
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                }
                Assert.Equal(1, server.Authentications); Assert.Equal(2, server.Queries); Assert.Equal(2, server.Resets);
                Assert.True(capture.Sources > 0);
                if (observer == "normal")
                {
                    var negotiated = Assert.Single(capture.Events, x => x.Key.EndsWith(".Negotiated", StringComparison.Ordinal)).Value;
                    Assert.Equal(compressed ? "compressed" : "mysql", negotiated["Layer"]);
                    Assert.Equal(compressed ? "compressed" : "mysql", negotiated["RequestedLayer"]);
                    Assert.Equal(false, negotiated["FullLinkTraceRequested"]);
                    Assert.Equal(false, negotiated["FullLinkTraceEnabled"]);
                    Assert.Equal(false, negotiated["ModernTraceFormat"]);
                    Assert.Contains(capture.Events, x => x.Key.EndsWith(".Frame", StringComparison.Ordinal) && Equals(x.Value["Layer"], compressed ? "compressed" : "mysql"));
                    Assert.Contains(capture.Events, x => x.Key.EndsWith(".Boundary", StringComparison.Ordinal));
                    Assert.DoesNotContain(capture.Events, x => x.Key.EndsWith(".Error", StringComparison.Ordinal));
                    CheckMetadata(capture.Events);
                }
                else Assert.Empty(capture.Events);
            }
            finally
            {
                if (connection != null) { connection.Close(); connection.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection }); connection.Dispose(); }
                context.Unload();
            }
        }

        public static IEnumerable<object[]> Ob20Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var kind in new[] { "enabled", "unrequested", "unsupported", "no-attributes" })
                        yield return new object[] { target, asynchronous, kind };
        }

        [Theory]
        [MemberData(nameof(Ob20Assets))]
        public async Task EveryAssetCorrelatesOb20AndInnerFrames(string target, bool asynchronous, string kind)
        {
            InitializeFixtureDiagnostics(); using var capture = new Capture("normal");
            await new Ob20ConsumerTests().EveryAssetNegotiatesOb20OrKeepsStandardFramesThroughPublicApis(target, asynchronous, kind);
            var negotiated = Assert.Single(capture.Events, x => x.Key.EndsWith(".Negotiated", StringComparison.Ordinal)).Value;
            Assert.Equal(kind == "enabled" ? "ob20" : "mysql", negotiated["Layer"]);
            Assert.Equal(kind == "unrequested" ? "mysql" : "ob20", negotiated["RequestedLayer"]);
            Assert.Equal(false, negotiated["FullLinkTraceRequested"]);
            Assert.Equal(false, negotiated["FullLinkTraceEnabled"]);
            Assert.Equal(false, negotiated["ModernTraceFormat"]);
            Assert.Equal(kind == "enabled", capture.Events.Any(x => x.Key.EndsWith(".Frame", StringComparison.Ordinal) && Equals(x.Value["Layer"], "ob20")));
            Assert.Contains(capture.Events, x => x.Key.EndsWith(".Frame", StringComparison.Ordinal) && Equals(x.Value["Layer"], "mysql"));
            Assert.Single(capture.Events.Select(x => x.Value["ChannelId"]).Distinct()); CheckMetadata(capture.Events);
        }

        private static void InitializeFixtureDiagnostics()
        { using var stream = new MemoryStream(); new PacketChannel(stream).BeginCommand(); }

        internal static void CheckMetadata(IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, object?>>> events)
        {
            var allowed = new[] { "ChannelId", "Layer", "Direction", "Sequence", "ExpectedSequence", "PayloadLength", "UncompressedLength", "RequestId", "Flags", "Stage", "Failure",
                "RequestedLayer", "FullLinkTraceRequested", "FullLinkTraceEnabled", "ModernTraceFormat" };
            foreach (var item in events)
                foreach (var field in item.Value)
                {
                    Assert.Contains(field.Key, allowed); Assert.True(field.Value is null or string or int or long or uint or bool);
                    var value = field.Value?.ToString() ?? ""; Assert.DoesNotContain("secret", value); Assert.DoesNotContain("private", value);
                }
        }

        internal sealed class Capture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
        {
            private readonly string _kind;
            private readonly IDisposable _all;
            private readonly List<IDisposable> _subscriptions = new();
            private bool _ready;
            internal int Sources { get; private set; }
            internal ConcurrentQueue<KeyValuePair<string, IReadOnlyDictionary<string, object?>>> Events { get; } = new();
            internal Capture(string kind) { _kind = kind; _all = DiagnosticListener.AllListeners.Subscribe(this); _ready = true; }
            public void OnNext(DiagnosticListener value)
            {
                if (!_ready || value.Name != "OceanBaseConnector.Protocol") return;
                Sources++;
                if (_kind == "source") throw new InvalidOperationException("private-source-error");
                _subscriptions.Add(value.Subscribe(this, (_, _, _) => _kind == "filter" ? throw new InvalidOperationException("private-filter-error") : true));
            }
            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (_kind == "write") throw new InvalidOperationException("private-observer-error");
                Events.Enqueue(new(value.Key, Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value.Value)));
            }
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void Dispose() { _all.Dispose(); foreach (var subscription in _subscriptions) subscription.Dispose(); }
        }
    }
}
