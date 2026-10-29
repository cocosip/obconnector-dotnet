using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        internal async Task<string> BuildClientPreparedSql(ParameterBindingPlan bindings, MySqlLiteralContext? context, bool multiStatement, bool asynchronous)
        {
            // Validate every binding and SQL restriction before consuming a caller's source.
            var sources = new List<Stream>();
            var text = ClientPreparedSql.Build(bindings, Mode, context, ClientEncoding, multiStatement,
                source => { sources.Add(source); return "NULL"; });
            if (sources.Count == 0) return text;
            long remaining = PacketChannel.DefaultMaxPacketSize - ClientEncoding.GetByteCount(text) - 1L + 4L * sources.Count;
            var literals = new List<string>(sources.Count);
            var buffer = new byte[8192];
            var prefix = Mode == OceanBaseCompatibilityMode.Oracle ? "'" : "X'";
            const string hex = "0123456789ABCDEF";
            foreach (var source in sources)
            {
                // Oracle uses quoted hex with implicit RAW/BLOB conversion, like OBStreamParameter.writeTo.
                var literal = new StringBuilder(prefix);
                remaining -= ClientEncoding.GetByteCount(prefix + "'");
                if (remaining < 0) throw new ArgumentException("Client prepared SQL exceeds the packet size limit.");
                while (true)
                {
                    EnsureReading();
                    var count = asynchronous ? await ReadSourceAsync(source, buffer, 0, buffer.Length).ConfigureAwait(false)
                        : ReadSource(source, buffer, 0, buffer.Length);
                    EnsureReading();
                    if (count < 0 || count > buffer.Length) throw new IOException("The parameter stream returned an invalid byte count.");
                    remaining -= 2L * count;
                    if (remaining < 0) throw new ArgumentException("Client prepared SQL exceeds the packet size limit.");
                    if (count == 0) break;
                    for (var i = 0; i < count; i++) literal.Append(hex[buffer[i] >> 4]).Append(hex[buffer[i] & 15]);
                }
                literals.Add(literal.Append('\'').ToString());
            }
            var index = 0;
            return ClientPreparedSql.Build(bindings, Mode, context, ClientEncoding, multiStatement, _ => literals[index++]);
        }
    }
}
