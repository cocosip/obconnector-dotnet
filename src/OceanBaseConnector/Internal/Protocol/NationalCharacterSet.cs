using System;
using System.Collections.Generic;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class NationalCharacterSet
    {
        internal static readonly Encoding Utf16 = new UnicodeEncoding(true, false, true);
        internal NationalCharacterSet(string? national, string? results, string? connection)
        { National = Normalize(national); Results = Normalize(results); Connection = Normalize(connection); }
        private static string? Normalize(string? value) => value == "" ? null : value;
        internal string? National { get; }
        internal string? Results { get; }
        internal string? Connection { get; }
        private static bool FallsBack(string? value) => value == null || value.Length == 0 || string.Equals(value, "binary", StringComparison.OrdinalIgnoreCase);
        internal Encoding ParameterEncoding => Resolve(FallsBack(National) ? Connection : National);
        internal Encoding ComplexParameterEncoding => Resolve(Connection);
        internal Encoding ResultEncoding(ushort collation)
        {
            if (!FallsBack(National)) return Resolve(National);
            if (!FallsBack(Results)) return Resolve(Results);
            return EncodingForCollation(collation);
        }
        internal static Encoding EncodingForCollation(ushort collation)
        {
            switch (collation)
            {
                case 33: case 45: case 46: case 83: case 224: case 255: return ResultColumn.Utf8;
                case 54: case 55: case 101: return Utf16;
                default: throw new NotSupportedException("The national column's source character set is not supported.");
            }
        }
        private static Encoding Resolve(string? name)
        {
            switch (name?.ToUpperInvariant())
            {
                case "UTF8": case "UTF8MB4": return ResultColumn.Utf8;
                case "UTF16": return Utf16;
                default: throw new NotSupportedException("The session national character encoding is not supported.");
            }
        }
        internal static NationalCharacterSet? FromVariables(IReadOnlyDictionary<string, string>? variables)
        {
            if (variables == null) return null;
            string? national = null, results = null, connection = null;
            var found = 0;
            foreach (var item in variables)
            {
                if (string.Equals(item.Key, "ncharacter_set_connection", StringComparison.OrdinalIgnoreCase)) { national = item.Value; found |= 1; }
                if (string.Equals(item.Key, "character_set_results", StringComparison.OrdinalIgnoreCase)) { results = item.Value; found |= 2; }
                if (string.Equals(item.Key, "character_set_connection", StringComparison.OrdinalIgnoreCase)) { connection = item.Value; found |= 4; }
            }
            return found == 7 ? new NationalCharacterSet(national, results, connection) : null;
        }
        internal Dictionary<string, string> Variables(string? zone = null)
        {
            var values = new Dictionary<string, string>
            {
                ["ncharacter_set_connection"] = National ?? "",
                ["character_set_results"] = Results ?? "",
                ["character_set_connection"] = Connection ?? ""
            };
            if (zone != null) values["time_zone"] = zone;
            return values;
        }
    }
}
