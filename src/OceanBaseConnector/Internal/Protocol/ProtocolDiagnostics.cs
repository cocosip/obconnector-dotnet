using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class ProtocolDiagnostics
    {
        private static long _nextId;
        // Avoid collectible ThreadStatic slots being reused while an old thread is still alive.
        private static readonly ThreadLocal<bool> Emitting = new ThreadLocal<bool>();
        private static readonly DiagnosticListener? Source = Create();
        private readonly long _id = Interlocked.Increment(ref _nextId);

        private static DiagnosticListener? Create()
        {
            var previous = Activity.Current;
            try { return new DiagnosticListener("OceanBaseConnector.Protocol"); }
            catch { return null; }
            finally { Restore(previous); }
        }

        internal void Frame(string layer, string direction, long length, int sequence, int expected,
            int originalLength = 0, long requestId = -1, uint flags = 0) =>
            Emit("Frame", layer, direction, length, sequence, expected, originalLength, requestId, flags);

        internal void Ob20Frame(string direction, byte[] header, byte expected) =>
            Frame("ob20", direction, Read32(header, 19), header[18], expected,
                header[4] | header[5] << 8 | header[6] << 16, header[15] | header[16] << 8 | header[17] << 16, Read32(header, 23));

        private static uint Read32(byte[] bytes, int offset) => (uint)bytes[offset] | (uint)bytes[offset + 1] << 8
            | (uint)bytes[offset + 2] << 16 | (uint)bytes[offset + 3] << 24;

        internal void Boundary(string stage) => Emit("Boundary", "mysql", "", stage: stage);

        internal void Negotiated(string layer, string requestedLayer, bool traceRequested, bool traceEnabled, bool modernTraceFormat)
            => Emit("Negotiated", layer, "", requestedLayer: requestedLayer, traceRequested: traceRequested,
                traceEnabled: traceEnabled, modernTraceFormat: modernTraceFormat);

        internal void Error(string layer, string direction, Exception? error) =>
            Emit("Error", layer, direction, failure: error == null ? "incomplete" : error is ProtocolException ? "protocol"
                : error is OperationCanceledException ? "canceled" : error is ObjectDisposedException ? "disposed"
                : error is IOException ? "io" : "error");

        private void Emit(string kind, string layer, string direction, long length = 0, int sequence = 0, int expected = 0,
            int originalLength = 0, long requestId = -1, uint flags = 0, string? failure = null, string? stage = null,
            string? requestedLayer = null, bool traceRequested = false, bool traceEnabled = false, bool modernTraceFormat = false)
        {
            if (Emitting.Value || Source == null) return;
            var previous = Activity.Current;
            Emitting.Value = true;
            try
            {
                var name = kind == "Frame" ? "OceanBaseConnector.Protocol.Frame"
                    : kind == "Error" ? "OceanBaseConnector.Protocol.Error" : kind == "Negotiated"
                        ? "OceanBaseConnector.Protocol.Negotiated" : "OceanBaseConnector.Protocol.Boundary";
                if (!Source.IsEnabled(name)) return;
                var values = new Dictionary<string, object?> { ["ChannelId"] = _id, ["Layer"] = layer };
                if (kind == "Boundary") values.Add("Stage", stage);
                else if (kind == "Negotiated")
                {
                    values.Add("RequestedLayer", requestedLayer); values.Add("FullLinkTraceRequested", traceRequested);
                    values.Add("FullLinkTraceEnabled", traceEnabled); values.Add("ModernTraceFormat", modernTraceFormat);
                }
                else
                {
                    values.Add("Direction", direction);
                    if (kind == "Error") values.Add("Failure", failure);
                    else
                    {
                        values.Add("Sequence", sequence); values.Add("ExpectedSequence", expected);
                        values.Add("PayloadLength", length); values.Add("UncompressedLength", originalLength);
                        values.Add("RequestId", requestId); values.Add("Flags", flags);
                    }
                }
                Source.Write(name, new ReadOnlyDictionary<string, object?>(values));
            }
            catch { } // Diagnostic observers are application code, never part of the wire protocol.
            finally { Restore(previous); Emitting.Value = false; }
        }

        private static void Restore(Activity? previous)
        { try { Activity.Current = previous; } catch { } }
    }
}
