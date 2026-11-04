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
            => Rewrite(ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, supplied));

        internal static ParameterBindingPlan Rewrite(ParameterBindingPlan bindings)
        {
            if (!Array.Exists(bindings.Parameters, RequiresRewrite)) return bindings;
            var positions = bindings.BindingPositions ?? throw new InvalidOperationException("Constructor rewriting requires parameter positions.");
            var parameters = new List<OceanBaseParameter>();
            var rewrittenPositions = new List<int>();
            var rewritten = new StringBuilder();
            var copiedThrough = 0;
            for (var i = 0; i < bindings.Parameters.Length; i++)
            {
                var parameter = bindings.Parameters[i];
                rewritten.Append(bindings.CommandText, copiedThrough, positions[i] - copiedThrough);
                if (RequiresRewrite(parameter))
                    AppendConstructor(rewritten, PreparedParameters.GetComplexType(parameter)!, (OceanBaseComplexValue)parameter.Value!, parameters, rewrittenPositions);
                else
                {
                    rewrittenPositions.Add(rewritten.Length);
                    rewritten.Append('?'); parameters.Add(parameter);
                }
                copiedThrough = positions[i] + 1;
            }
            rewritten.Append(bindings.CommandText, copiedThrough, bindings.CommandText.Length - copiedThrough);
            return new ParameterBindingPlan(rewritten.ToString(), parameters.ToArray(), rewrittenPositions.ToArray());
        }

        private static void AppendConstructor(StringBuilder sql, OceanBaseComplexType type, OceanBaseComplexValue value,
            List<OceanBaseParameter> hidden, List<int> positions)
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
                        AppendConstructor(sql, member.ComplexType!, nested, hidden, positions);
                    }
                    continue;
                }
                if (memberValue == null || memberValue == DBNull.Value) { sql.Append("NULL"); continue; }
                hidden.Add(CreateMemberParameter(member.Layout.TypeCode, memberValue));
                positions.Add(sql.Length); sql.Append('?');
            }
            sql.Append(')');
        }

        private static OceanBaseParameter CreateMemberParameter(byte typeCode, object value)
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
            return parameter;
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

        private static object Require(object value, Type type)
            => type.IsInstanceOfType(value) ? value : throw new ArgumentException("The complex member value does not match its declared type.");

        private static OceanBaseLob RequireLob(object value, bool character)
            => value is OceanBaseLob lob && lob.IsCharacter == character ? lob : throw new ArgumentException("A named LOB member requires a matching locator value.");

    }
}
