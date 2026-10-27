using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace OceanBaseConnector.Acceptance;

// Keep fixed scalar observations only; query outcomes validate bodies/checksums through the driver.
internal sealed class Ob20TraceCapture : IObserver<DiagnosticListener>, IDisposable
{
    private const string Negotiated = "OceanBaseConnector.Protocol.Negotiated";
    private const string Frame = "OceanBaseConnector.Protocol.Frame";
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _enabled = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly IDisposable _all;
    private readonly bool _ob20, _trace, _modern, _traceRequested;
    private DiagnosticListener? _listener;
    private long _channel;
    private int _negotiations;
    private bool _invalid, _queries, _read, _write, _extra, _disposed;

    public Ob20TraceCapture(string profile)
    {
        _ob20 = profile != "declined-ob20";
        _trace = profile is "trace-legacy" or "trace-modern";
        _modern = profile == "trace-modern";
        _traceRequested = profile != "ob20";
        _enabled.Value = true;
        _all = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != "OceanBaseConnector.Protocol") return;
        lock (_gate)
            if (!_disposed) _subscriptions.Add(listener.Subscribe(new Events(this, listener),
                (name, _, _) => name is Negotiated or Frame));
    }

    private void Record(DiagnosticListener listener, KeyValuePair<string, object?> item)
    {
        if (!_enabled.Value) return;
        lock (_gate)
        {
            if (_disposed || item.Key is not (Negotiated or Frame)) return;
            if (item.Value is not IReadOnlyDictionary<string, object?> f ||
                !f.TryGetValue("ChannelId", out var id) || id is not long channel)
            { _invalid = true; return; }
            if (item.Key == Negotiated)
            {
                _negotiations++;
                _listener = listener; _channel = channel;
                if (f.Count != 6 || !String(f, "RequestedLayer", "ob20") || !String(f, "Layer", _ob20 ? "ob20" : "mysql") ||
                    !Boolean(f, "FullLinkTraceRequested", _traceRequested) || !Boolean(f, "FullLinkTraceEnabled", _trace) ||
                    !Boolean(f, "ModernTraceFormat", _modern)) _invalid = true;
                return;
            }
            if (!_queries) return;
            if (!ReferenceEquals(_listener, listener) || _channel != channel ||
                !f.TryGetValue("Layer", out var layer) || layer is not ("mysql" or "ob20") ||
                !f.TryGetValue("Direction", out var direction) || direction is not ("read" or "write") ||
                !f.TryGetValue("Flags", out var flags) || flags is not uint bits)
            { _invalid = true; return; }
            if (layer is "ob20" && !_ob20) { _invalid = true; return; }
            if (!Equals(layer, _ob20 ? "ob20" : "mysql")) return; // OB20 also emits inner MySQL events.
            if (direction is "read") _read = true;
            else
            {
                _write = true;
                if (_ob20)
                {
                    if (((bits & 8) != 0) != _modern || !_trace && (bits & 1) != 0) _invalid = true;
                    _extra |= (bits & 1) != 0;
                }
            }
        }
    }

    public void BeginQueries() { lock (_gate) _queries = true; }
    public void Verify()
    {
        lock (_gate)
        {
            if (_invalid || _negotiations != 1)
                throw new AssertionFailure("Protocol negotiation or command frame identity/format does not match the selected profile.");
            if (!_read || !_write) throw new AssertionFailure("Control queries require actual bidirectional frames in the selected protocol.");
            if (_trace && !_extra) throw new AssertionFailure("Trace profile requires outgoing trace extra-info; server sampling/control did not produce trace data.");
        }
    }

    private static bool String(IReadOnlyDictionary<string, object?> f, string key, string expected)
        => f.TryGetValue(key, out var value) && value is string text && text == expected;
    private static bool Boolean(IReadOnlyDictionary<string, object?> f, string key, bool expected)
        => f.TryGetValue(key, out var value) && value is bool flag && flag == expected;
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        _enabled.Value = false;
        IDisposable[] subscriptions;
        lock (_gate)
        {
            _disposed = true;
            subscriptions = _subscriptions.ToArray(); _subscriptions.Clear();
        }
        _all.Dispose();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
    private sealed class Events(Ob20TraceCapture owner, DiagnosticListener listener) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> item) => owner.Record(listener, item);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
