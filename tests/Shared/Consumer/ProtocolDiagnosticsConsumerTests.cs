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
    public sealed partial class ProtocolDiagnosticsConsumerTests
    {

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
