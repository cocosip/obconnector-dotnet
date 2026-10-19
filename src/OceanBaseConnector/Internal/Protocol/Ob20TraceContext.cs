using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    // Per-connection, single-owner FLT state; only fixed labels, IDs and times cross this boundary.
    internal sealed class Ob20TraceContext
    {
        internal const int MaxExtraLength = 64 * 1024;
        private readonly bool _modern;
        private readonly Random _sampling = new Random();
        private Policy _policy = new Policy { Record = 2 };
        private string? _pendingLog;
        private bool _pendingShow;
        private string? _traceId;
        private string? _spanId;
        private string? _parentId;
        private long _startUs;
        private long _started;
        private bool _force;
        private Policy _requestPolicy;

        internal Ob20TraceContext(bool modern) => _modern = modern;
        internal bool UsesModernFormat => _modern;

        internal byte[] BeginRequest(ActivityContext context)
        {
            using var body = new MemoryStream();
            if (_pendingLog != null)
            {
                Tlv(body, 1, Field(1, Encoding.UTF8.GetBytes(_pendingLog + "\0")));
                if (_pendingShow) Tlv(body, 2005, Field(2050, Encoding.UTF8.GetBytes("[" + _pendingLog + "]\0")));
                _pendingLog = null; _pendingShow = false;
            }
            _traceId = null;
            if (_policy.Show || (_policy.Level > 0 && _policy.Record != 4 && _sampling.NextDouble() < _policy.Sample))
            {
                _traceId = context.TraceId == default ? ActivityTraceId.CreateRandom().ToHexString() : context.TraceId.ToHexString();
                _parentId = context.SpanId == default ? new string('0', 32) : new string('0', 16) + context.SpanId.ToHexString();
                _spanId = Guid.NewGuid().ToString("N");
                _force = _policy.Show || _policy.Record == 1 || (_policy.Record == 3 && _sampling.NextDouble() < _policy.PrintSample);
                _startUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000; _started = Stopwatch.GetTimestamp(); _requestPolicy = _policy;
                using var span = new MemoryStream();
                Tlv(span, 2030, new byte[] { 1 }); Tlv(span, 2031, new[] { _force ? (byte)1 : (byte)0 });
                // UUIDs are opaque 16-byte IDs on the wire; use the server's printed order for W3C IDs.
                Tlv(span, 2032, Hex(_traceId)); Tlv(span, 2033, new byte[] { 0 }); Tlv(span, 2034, Hex(_spanId));
                Tlv(body, 2004, span.ToArray());
            }
            if (body.Length == 0) return Array.Empty<byte>();
            using var extra = new MemoryStream();
            if (_modern) Tlv(extra, 2003, body.ToArray());
            else { Varchar(extra, Encoding.ASCII.GetBytes("full_trc")); Varchar(extra, body.ToArray()); }
            return extra.ToArray();
        }

        internal void CompleteRequest()
        {
            if (_traceId == null) return;
            var elapsedUs = (long)((Stopwatch.GetTimestamp() - _started) * (1_000_000d / Stopwatch.Frequency));
            if (_force || ((_requestPolicy.Record == 2 || _requestPolicy.Record == 3) && _requestPolicy.SlowUs > 0 && elapsedUs >= _requestPolicy.SlowUs))
            {
                _pendingLog = "{\"trace_id\":\"" + Uuid(_traceId) + "\",\"name\":\"oceanbase_dotnet\",\"id\":\"" + Uuid(_spanId!) +
                    "\",\"parent_id\":\"" + Uuid(_parentId!) + "\",\"start_ts\":" + _startUs.ToString(CultureInfo.InvariantCulture) +
                    ",\"end_ts\":" + (_startUs + elapsedUs).ToString(CultureInfo.InvariantCulture) + ",\"is_follow\":false}";
                _pendingShow = _requestPolicy.Show;
            }
            _traceId = _spanId = _parentId = null;
        }

        internal void ReadExtra(byte[] bytes, bool modern)
        {
            if (bytes.Length > MaxExtraLength) throw new ProtocolException("OB20 trace extra info exceeds its limit.");
            var cursor = new ByteCursor(bytes); var policy = _policy; var groups = new HashSet<ushort>();
            while (cursor.HasRemaining)
            {
                byte[] value; bool fullTrace;
                if (modern) { value = ReadTlv(cursor, out var key); fullTrace = key == 2003; }
                else { var key = ReadVarchar(cursor); value = ReadVarchar(cursor); fullTrace = string.Equals(Encoding.ASCII.GetString(key), "full_trc", StringComparison.OrdinalIgnoreCase); }
                if (fullTrace) ReadTrace(new ByteCursor(value), ref policy, groups);
            }
            _policy = policy;
        }

        private static void ReadTrace(ByteCursor cursor, ref Policy policy, HashSet<ushort> groups)
        {
            while (cursor.HasRemaining)
            {
                var value = ReadTlv(cursor, out var group);
                if (group != 2002 && group != 2003 && group != 2004) continue;
                if (!groups.Add(group)) throw new ProtocolException("Duplicate OB20 trace info group.");
                var fields = new ByteCursor(value); var seen = new HashSet<ushort>(); long? start = null; long? end = null;
                while (fields.HasRemaining)
                {
                    var bytes = ReadTlv(fields, out var id);
                    var known = group == 2003 ? id >= 2020 && id <= 2025 : group == 2002 ? id == 2010 || id == 2011 : id >= 2030 && id <= 2034;
                    if (known && !seen.Add(id)) throw new ProtocolException("Duplicate OB20 trace info field.");
                    var field = new ByteCursor(bytes);
                    if (group == 2003)
                    {
                        switch (id)
                        {
                            case 2020: Length(bytes, 1); policy.Level = unchecked((sbyte)field.ReadUInt8()); break;
                            case 2021: policy.Sample = Probability(bytes); break;
                            case 2022:
                                Length(bytes, 1); policy.Record = field.ReadUInt8();
                                if (policy.Record < 1 || policy.Record > 4) throw new ProtocolException("Invalid OB20 trace record policy.");
                                break;
                            case 2023: policy.PrintSample = Probability(bytes); break;
                            case 2024: policy.SlowUs = Time(bytes); break;
                            case 2025: policy.Show = Boolean(bytes); break;
                        }
                    }
                    else if (group == 2002)
                    {
                        if (id == 2010) start = Time(bytes);
                        else if (id == 2011) end = Time(bytes);
                    }
                    else
                    {
                        if (id == 2030 || id == 2031) Boolean(bytes);
                        else if (id == 2032 || id == 2034) Length(bytes, 16);
                        else if (id == 2033) { Length(bytes, 1); if (bytes[0] > 1) throw new ProtocolException("Invalid OB20 trace reference type."); }
                    }
                }
                if (start.HasValue && end.HasValue && end.Value < start.Value) throw new ProtocolException("Invalid OB20 trace query time range.");
            }
        }

        private static byte[] ReadTlv(ByteCursor cursor, out ushort type)
        {
            type = cursor.ReadUInt16(); var length = cursor.ReadUInt32();
            if (length > (uint)cursor.Remaining) throw new ProtocolException("OB20 trace TLV exceeds its container.");
            return cursor.ReadBytes((int)length);
        }
        private static void Length(byte[] bytes, int expected)
        { if (bytes.Length != expected) throw new ProtocolException("Invalid OB20 trace scalar length."); }
        private static double Probability(byte[] bytes)
        {
            Length(bytes, 8); var value = BitConverter.Int64BitsToDouble(unchecked((long)new ByteCursor(bytes).ReadUInt64()));
            if (double.IsNaN(value) || value < 0 || value > 1) throw new ProtocolException("Invalid OB20 trace sample probability.");
            return value;
        }
        private static long Time(byte[] bytes)
        {
            Length(bytes, 8); var value = new ByteCursor(bytes).ReadUInt64();
            if (value > long.MaxValue) throw new ProtocolException("Invalid OB20 trace time value."); return (long)value;
        }
        private static bool Boolean(byte[] bytes)
        { Length(bytes, 1); if (bytes[0] > 1) throw new ProtocolException("Invalid OB20 trace boolean."); return bytes[0] == 1; }
        private static byte[] ReadVarchar(ByteCursor cursor)
        {
            if (cursor.ReadUInt8() != 22) throw new ProtocolException("Unsupported legacy OB20 extra object type.");
            cursor.Skip(3); uint length = 0; var shift = 0;
            while (true)
            {
                var value = cursor.ReadUInt8();
                if (shift > 14 || (shift == 14 && (value & 127) > 4)) throw new ProtocolException("Legacy OB20 string length exceeds its limit.");
                length |= (uint)(value & 127) << shift;
                if ((value & 128) == 0)
                {
                    break;
                }
                shift += 7;
            }
            if (length >= (uint)cursor.Remaining) throw new ProtocolException("Truncated legacy OB20 string.");
            var bytes = cursor.ReadBytes((int)length);
            if (cursor.ReadUInt8() != 0) throw new ProtocolException("Invalid legacy OB20 string terminator."); return bytes;
        }
        private static void Varchar(Stream stream, byte[] bytes)
        {
            stream.Write(new byte[] { 22, 2, 0, 255 }, 0, 4); var length = bytes.Length;
            while (length >= 128) { stream.WriteByte((byte)(length | 128)); length >>= 7; }
            stream.WriteByte((byte)length); stream.Write(bytes, 0, bytes.Length); stream.WriteByte(0);
        }
        private static byte[] Field(ushort type, byte[] bytes)
        { using var stream = new MemoryStream(); Tlv(stream, type, bytes); return stream.ToArray(); }
        private static void Tlv(Stream stream, ushort type, byte[] bytes)
        {
            stream.WriteByte((byte)type); stream.WriteByte((byte)(type >> 8));
            for (var i = 0; i < 4; i++) stream.WriteByte((byte)(bytes.Length >> (i * 8)));
            stream.Write(bytes, 0, bytes.Length);
        }
        private static byte[] Hex(string text)
        {
            var bytes = new byte[text.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(text.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        private static string Uuid(string hex) => hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
        private struct Policy
        {
            internal sbyte Level;
            internal double Sample;
            internal byte Record;
            internal double PrintSample;
            internal long SlowUs;
            internal bool Show;
        }
    }
}
