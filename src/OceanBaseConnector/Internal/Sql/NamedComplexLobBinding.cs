using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Sql
{
    internal static class NamedComplexLobBinding
    {
        internal static bool RequiresRewrite(OceanBaseParameter parameter)
        {
            if (parameter.Direction != ParameterDirection.Input || parameter.Value is not OceanBaseComplexValue)
                return false;
            var complex = PreparedParameters.GetComplexType(parameter);
            return complex != null && !complex.IsAnonymous && parameter.Value is OceanBaseComplexValue value &&
                ContainsLocator(complex, value);
        }

        internal static ParameterBindingPlan Rewrite(string sql, OceanBaseParameter[] supplied)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (supplied == null) throw new ArgumentNullException(nameof(supplied));

            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rewrite = new bool[supplied.Length];
            for (var i = 0; i < supplied.Length; i++)
            {
                var parameter = supplied[i];
                var name = ParameterBindingPlan.NormalizeName(parameter.ParameterName);
                if (names.ContainsKey(name)) throw new ArgumentException("Oracle parameter names must be unique after normalization.");
                names.Add(name, i);
                var complex = RequiresRewrite(parameter) ? PreparedParameters.GetComplexType(parameter) : null;
                if (complex != null && parameter.Value is OceanBaseComplexValue value)
                {
                    rewrite[i] = true;
                }
            }

            if (!Array.Exists(rewrite, static value => value)) return ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, supplied);

            var hidden = new List<OceanBaseParameter>();
            var usedNames = new HashSet<string>(names.Keys, StringComparer.OrdinalIgnoreCase);
            var rewritten = new StringBuilder();
            var copiedThrough = 0;
            var referenced = new bool[supplied.Length];
            var position = 0;
            while (position < sql.Length)
            {
                var c = sql[position];
                if (c == '\'' || c == '"') { position = ParameterBindingPlan.SkipQuote(sql, position, c); continue; }
                var alternative = (c == 'q' || c == 'Q') ? position :
                    (c == 'n' || c == 'N') && position + 1 < sql.Length && (sql[position + 1] == 'q' || sql[position + 1] == 'Q') ? position + 1 : -1;
                if (alternative >= 0 && alternative + 1 < sql.Length && sql[alternative + 1] == '\'')
                {
                    if (position > 0 && ParameterBindingPlan.IsIdentifierPart(sql[position - 1])) throw new ArgumentException("The Oracle literal syntax is not supported.");
                    position = ParameterBindingPlan.SkipAlternativeQuote(sql, alternative); continue;
                }
                if (c == '-' && position + 1 < sql.Length && sql[position + 1] == '-')
                {
                    position += 2; while (position < sql.Length && sql[position] != '\r' && sql[position] != '\n') position++; continue;
                }
                if (c == '/' && position + 1 < sql.Length && sql[position + 1] == '*')
                { position = SkipComment(sql, position + 2); continue; }
                if (c != ':') { position++; continue; }
                if (position + 1 < sql.Length && sql[position + 1] == '=') { position += 2; continue; }

                var start = position++;
                if (position == sql.Length || !OracleCommandPlan.IsNameStart(sql[position])) throw new ArgumentException("The Oracle parameter syntax is not supported.");
                position++;
                while (position < sql.Length && OracleCommandPlan.IsNamePart(sql[position])) position++;
                if (position < sql.Length && ParameterBindingPlan.IsIdentifierPart(sql[position])) throw new ArgumentException("The Oracle parameter syntax is not supported.");
                var name = sql.Substring(start + 1, position - start - 1);
                if (!names.TryGetValue(name, out var index) || !rewrite[index]) continue;

                var parameter = supplied[index];
                var type = PreparedParameters.GetComplexType(parameter)!;
                var value = (OceanBaseComplexValue)parameter.Value!;
                rewritten.Append(sql, copiedThrough, start - copiedThrough);
                AppendConstructor(rewritten, type, value, hidden, usedNames);
                copiedThrough = position;
                referenced[index] = true;
            }

            for (var i = 0; i < rewrite.Length; i++)
                if (rewrite[i] && !referenced[i]) throw new ArgumentException("One or more Oracle parameters are not referenced by the command.");

            rewritten.Append(sql, copiedThrough, sql.Length - copiedThrough);
            var parameters = new OceanBaseParameter[hidden.Count + supplied.Length - Count(rewrite)];
            hidden.CopyTo(parameters, 0);
            var offset = hidden.Count;
            for (var i = 0; i < supplied.Length; i++) if (!rewrite[i]) parameters[offset++] = supplied[i];
            return ParameterBindingPlan.Create(rewritten.ToString(), OceanBaseCompatibilityMode.Oracle, parameters);
        }

        private static void AppendConstructor(StringBuilder sql, OceanBaseComplexType type, OceanBaseComplexValue value,
            List<OceanBaseParameter> hidden, HashSet<string> usedNames)
        {
            if (type.IsAnonymous || type.SchemaName.Length == 0 || type.TypeName.Length == 0)
                throw new NotSupportedException("Named complex LOB rewriting requires a named Oracle type.");
            if (!ReferenceEquals(type, value.Type)) throw new ArgumentException("The complex parameter value must use its declared type descriptor.");

            sql.Append('"').Append(type.SchemaName.Replace("\"", "\"\""))
                .Append("\".\"").Append(type.TypeName.Replace("\"", "\"\""))
                .Append("\"(");
            var values = value.GetValues();
            for (var i = 0; i < values.Length; i++)
            {
                if (i != 0) sql.Append(',');
                var member = type.Members[type.IsCollection ? 0 : i];
                var memberValue = values[i];
                if (member.Layout.TypeCode == 160)
                {
                    if (memberValue == null || memberValue == DBNull.Value)
                    {
                        if (type.IsCollection && !member.ComplexType!.IsCollection)
                            throw new NotSupportedException("Named complex constructors cannot preserve NULL object identity inside collections.");
                        sql.Append("NULL");
                    }
                    else
                    {
                        if (!(memberValue is OceanBaseComplexValue nested)) throw new ArgumentException("A nested complex value requires an object array.");
                        AppendConstructor(sql, member.ComplexType!, nested, hidden, usedNames);
                    }
                    continue;
                }
                if (memberValue == null || memberValue == DBNull.Value) { sql.Append("NULL"); continue; }
                var parameter = CreateMemberParameter(member.Layout.TypeCode, memberValue, hidden, usedNames);
                sql.Append(':').Append(parameter.ParameterName);
            }
            sql.Append(')');
        }

        private static OceanBaseParameter CreateMemberParameter(byte typeCode, object value, List<OceanBaseParameter> hidden, HashSet<string> usedNames)
        {
            OceanBaseParameter parameter;
            switch (typeCode)
            {
                case 0:
                    var number = value is OceanBaseDecimal exact ? exact : new OceanBaseDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                    parameter = new OceanBaseParameter { DbType = DbType.Decimal, Value = number }; break;
                case 253:
                case 254: parameter = new OceanBaseParameter { DbType = DbType.String, Value = Require(value, typeof(string)) }; break;
                case 207: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = Require(value, typeof(string)) }; break;
                case 208: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.NChar, Value = Require(value, typeof(string)) }; break;
                case 203: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Binary, Value = Require(value, typeof(byte[])) }; break;
                case 210: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Blob, Value = RequireLob(value, false) }; break;
                case 211: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Clob, Value = RequireLob(value, true) }; break;
                case 12: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.OracleDate, Value = Require(value, typeof(DateTime)) }; break;
                case 202: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Timestamp, Value = value }; break;
                case 204: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.IntervalYearToMonth, Value = value }; break;
                case 205: parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.IntervalDayToSecond, Value = value }; break;
                default: throw new NotSupportedException("Named complex LOB rewriting does not support this member type.");
            }
            var prefix = "__obc_named_lob_";
            var index = hidden.Count;
            var name = prefix + index.ToString(CultureInfo.InvariantCulture);
            while (!usedNames.Add(name)) name = prefix + (++index).ToString(CultureInfo.InvariantCulture);
            parameter.ParameterName = name; hidden.Add(parameter); return parameter;
        }

        private static bool ContainsLocator(OceanBaseComplexType type, OceanBaseComplexValue value)
        {
            var values = value.GetValues();
            for (var i = 0; i < values.Length; i++)
            {
                var member = type.Members[type.IsCollection ? 0 : i];
                if (values[i] is OceanBaseLob) return true;
                if (member.Layout.TypeCode == 160 && values[i] is OceanBaseComplexValue nested && ContainsLocator(member.ComplexType!, nested)) return true;
            }
            return false;
        }

        private static int Count(bool[] values)
        { var count = 0; foreach (var value in values) if (value) count++; return count; }

        private static object Require(object value, Type type)
            => type.IsInstanceOfType(value) ? value : throw new ArgumentException("The complex member value does not match its declared type.");

        private static OceanBaseLob RequireLob(object value, bool character)
            => value is OceanBaseLob lob && lob.IsCharacter == character ? lob : throw new ArgumentException("A named LOB member requires a matching locator value.");

        private static int SkipComment(string sql, int position)
        {
            for (var i = position; i + 1 < sql.Length; i++) if (sql[i] == '*' && sql[i + 1] == '/') return i + 2;
            throw new ArgumentException("CommandText contains an unterminated comment.");
        }
    }
}
