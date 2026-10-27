using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace OceanBaseConnector.Acceptance;

// Compare public, locally generated channel identities without retaining frame data or server IDs.
internal sealed class ProtocolChannelCapture : IObserver<DiagnosticListener>, IDisposable
{
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _enabled = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly IDisposable _all;
    private DiagnosticListener? _listener;
    private long _channel;
    private bool _different;
    private bool _disposed;

    public ProtocolChannelCapture()
    {
        _enabled.Value = true;
        _all = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public bool HasSingleChannel { get { lock (_gate) return _listener != null && !_different; } }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != "OceanBaseConnector.Protocol") return;
        lock (_gate)
            if (!_disposed) _subscriptions.Add(listener.Subscribe(new Frames(this, listener),
                (name, _, _) => name == "OceanBaseConnector.Protocol.Frame"));
    }

    private void Record(DiagnosticListener listener, KeyValuePair<string, object?> item)
    {
        if (!_enabled.Value || item.Value is not IReadOnlyDictionary<string, object?> fields ||
            !fields.TryGetValue("ChannelId", out var value) || value is not long channel) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_listener == null) { _listener = listener; _channel = channel; }
            else if (!ReferenceEquals(_listener, listener) || _channel != channel) _different = true;
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        _enabled.Value = false;
        IDisposable[] subscriptions;
        lock (_gate)
        {
            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }
        _all.Dispose();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    private sealed class Frames(ProtocolChannelCapture owner, DiagnosticListener listener) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> item) => owner.Record(listener, item);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
