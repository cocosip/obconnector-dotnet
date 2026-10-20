using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector.Internal.Sql
{
    internal sealed class OracleCommandPlan
    {
        private OracleCommandPlan(ParameterBindingPlan bindings, OceanBaseParameter[] outputs, OceanBaseComplexType?[] complexTypes)
        {
            Bindings = bindings; Outputs = outputs; _outputTypes = new OceanBaseDbType?[outputs.Length]; _outputComplexTypes = complexTypes;
            for (var i = 0; i < outputs.Length; i++)
            {
                var parameter = outputs[i]; var type = parameter.OceanBaseDbType;
                if (!type.HasValue && parameter.Direction == ParameterDirection.InputOutput && parameter.DbType == DbType.Object && parameter.Value is OceanBaseLob lob)
                    type = lob.IsCharacter ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
                _outputTypes[i] = type;
            }
        }

        internal ParameterBindingPlan Bindings { get; }
        internal OceanBaseParameter[] Outputs { get; }
        internal Action<uint>? StatementCreated { get; set; }
        internal OracleCursorOwner? Cursors { get; set; }
        internal OracleArrayBindingPlan? ArrayBinding { get; private set; }
        private object[]? _values;
        private readonly OceanBaseDbType?[] _outputTypes;
        private readonly OceanBaseComplexType?[] _outputComplexTypes;

        internal void ValidateOutputColumn(int ordinal, ResultColumn column)
        {
            if (ArrayBinding != null) { ArrayBinding.ValidateColumn(ordinal, column); return; }
            var expected = _outputComplexTypes[ordinal];
            if (expected == null)
            {
                if (column.TypeCode == 160) throw new NotSupportedException("A complex output requires an explicit type declaration.");
            }
            else column.BindComplexOutput(expected);
        }

        internal bool BeginResponse(OraclePrepareExecutePacket header)
        {
            StatementCreated?.Invoke(header.StatementId);
            if (ArrayBinding != null)
            {
                if (!header.HasResultSet || header.ColumnCount != Outputs.Length + 3 ||
                    !ArrayBinding.AcceptsFlags(header.ExtendFlags))
                    throw new ProtocolException("Oracle array binding metadata does not match the request.");
                return true;
            }
            if (header.ExtendFlags != 0 && header.ExtendFlags != 1 && header.ExtendFlags != 3 && header.ExtendFlags != 4)
                throw new NotSupportedException("This Oracle PrepareExecute extension is not supported.");
            var output = header.ExtendFlags != 0;
            if (output && (Outputs.Length == 0 || !header.HasResultSet || header.ColumnCount != Outputs.Length))
                throw new ProtocolException("Oracle output metadata does not match the bound output parameters.");
            return output;
        }

        internal void ValidateOutputColumns(ResultColumn[] columns)
        {
            if (Outputs.Length == 0 || columns.Length != Outputs.Length)
                throw new ProtocolException("Oracle output metadata does not match the bound output parameters.");
            for (var i = 0; i < columns.Length; i++) ValidateOutputColumn(i, columns[i]);
        }

        internal void CaptureOutput(ResultColumn[] columns, byte[]?[] row)
        {
            if (ArrayBinding != null) { ArrayBinding.Capture(columns, row); return; }
            if (_values != null) throw new NotSupportedException("Scalar Oracle output parameters cannot contain multiple rows.");
            var values = new object[Outputs.Length];
            for (var i = 0; i < values.Length; i++)
            {
                ValidateOutputColumn(i, columns[i]);
                var declared = _outputTypes[i];
                if (row[i] != null && ((declared == OceanBaseDbType.Blob && columns[i].TypeCode != 210) ||
                    (declared == OceanBaseDbType.Clob && columns[i].TypeCode != 211) ||
                    ((columns[i].TypeCode == 210 || columns[i].TypeCode == 211) && declared != OceanBaseDbType.Blob && declared != OceanBaseDbType.Clob)))
                    throw new ProtocolException("LOB output metadata must match its declared BLOB or CLOB type.");
                if (declared == OceanBaseDbType.RefCursor && columns[i].TypeCode != 163 && row[i] != null)
                    throw new ProtocolException("A REF CURSOR output requires cursor metadata.");
                // PL outputs use precision 0 / scale -1 for an unconstrained runtime NUMBER.
                values[i] = columns[i].TypeCode == 163 && row[i] != null
                    ? (object)(Cursors ?? throw new NotSupportedException("A REF CURSOR requires its originating command.")).Create(new ByteCursor(row[i]!).ReadUInt32())
                    : row[i] != null && columns[i].IsDecimal && columns[i].Precision == 0 && columns[i].Scale == -1
                        ? columns[i].DecodeDecimal(row[i]!, binary: true, constrain: false)
                        : columns[i].DecodeBinary(row[i]);
            }
            _values = values;
        }

        internal void FinishOutput()
        {
            if (ArrayBinding != null) return;
            if (_values != null) return;
            _values = new object[Outputs.Length];
            for (var i = 0; i < _values.Length; i++) _values[i] = DBNull.Value;
        }

        internal void PublishOutputs()
        {
            if (ArrayBinding != null)
            {
                var result = ArrayBinding.Completed ?? throw new ProtocolException("Array binding has no terminal result.");
                var values = new object[Outputs.Length][][];
                for (var i = 0; i < values.Length; i++) values[i] = new object[result.Items.Count][];
                for (var j = 0; j < result.Items.Count; j++)
                {
                    var rows = result.Items[j].OutputRows;
                    for (var i = 0; i < values.Length; i++)
                    {
                        values[i][j] = new object[rows.Count];
                        for (var k = 0; k < rows.Count; k++) values[i][j][k] = rows[k][i];
                    }
                }
                for (var i = 0; i < values.Length; i++) Outputs[i].Value = values[i];
                ArrayBinding.Published = true;
                return;
            }
            if (Outputs.Length == 0) return;
            if (_values == null) throw new ProtocolException("Oracle execution completed without its bound output values.");
            Cursors?.Publish();
            for (var i = 0; i < _values.Length; i++) Outputs[i].Value = _values[i];
        }

        internal static OracleCommandPlan Create(string text, CommandType type, OceanBaseParameterCollection parameters, int arrayCount = 0)
        {
            var pl = arrayCount != 0 && (type == CommandType.StoredProcedure || IsArrayPl(text));
            OceanBaseParameter? returned = null;
            foreach (OceanBaseParameter parameter in parameters)
            {
                if (parameter.Direction != ParameterDirection.ReturnValue) continue;
                if (returned != null || type != CommandType.StoredProcedure)
                    throw new ArgumentException("A single ReturnValue parameter requires CommandType.StoredProcedure.");
                returned = parameter;
            }
            if (type == CommandType.StoredProcedure)
            {
                ValidateProcedureName(text);
                var sql = new StringBuilder("BEGIN ");
                if (returned != null) sql.Append(':').Append(BindingName(returned.ParameterName)).Append(" := ");
                sql.Append(text).Append('(');
                var first = true;
                foreach (OceanBaseParameter parameter in parameters)
                {
                    if (ReferenceEquals(parameter, returned)) continue;
                    if (!first) sql.Append(", ");
                    sql.Append(':').Append(BindingName(parameter.ParameterName)); first = false;
                }
                text = sql.Append("); END;").ToString();
            }
            else if (type != CommandType.Text) throw new NotSupportedException("This CommandType is not supported.");
            var original = ParameterBindingPlan.Create(text, OceanBaseCompatibilityMode.Oracle, parameters, allowOutputs: true);
            var inputs = new OceanBaseParameter[original.Parameters.Length];
            var outputs = new List<OceanBaseParameter>();
            var complexTypes = new List<OceanBaseComplexType?>();
            for (var i = 0; i < inputs.Length; i++)
            {
                var parameter = original.Parameters[i];
                if (parameter.Direction == ParameterDirection.InputOutput && parameter.Value is System.IO.Stream)
                    throw new NotSupportedException("Stream parameters require Input direction.");
                if (parameter.OceanBaseDbType == OceanBaseDbType.RefCursor &&
                    (parameter.Direction == ParameterDirection.Input || parameter.Direction == ParameterDirection.InputOutput || parameter.Size != 0 || parameter.Precision != 0 || parameter.Scale != 0))
                    throw new NotSupportedException("REF CURSOR parameters require Output or ReturnValue and no scalar size or precision.");
                var copy = inputs[i] = parameter.Snapshot();
                if (parameter.Direction == ParameterDirection.Output || parameter.Direction == ParameterDirection.ReturnValue)
                {
                    if (parameter.ComplexType == null && parameter.DbType == DbType.Object && parameter.Value is OceanBaseComplexValue)
                        throw new ArgumentException("A complex Output or ReturnValue requires ComplexType.");
                    copy.Value = null;
                }
                if (arrayCount != 0)
                {
                    copy.Direction = ParameterDirection.Input;
                    var arrayType = PreparedParameters.GetComplexType(copy);
                    if (arrayType == null || !arrayType.IsAnonymous || arrayType.Context == null)
                        throw new ArgumentException("Array binding parameters require a factory anonymous array type.");
                    copy.ComplexType = arrayType;
                    if (parameter.Direction == ParameterDirection.Output || parameter.Direction == ParameterDirection.ReturnValue)
                        copy.Value = new OceanBaseComplexValue(arrayType, new object?[arrayCount]);
                    if (!(copy.Value is OceanBaseComplexValue arrayValue) || arrayValue.Count != arrayCount)
                        throw new ArgumentException("Array binding columns must match the requested positive row count.");
                }
                if (parameter.OceanBaseDbType == OceanBaseDbType.RefCursor) copy.OceanBaseDbType = OceanBaseDbType.Null;
                var complex = PreparedParameters.GetComplexType(copy);
                if (complex != null) copy.ComplexType = complex;
                if (parameter.Direction != ParameterDirection.Input) { outputs.Add(parameter); complexTypes.Add(complex); }
                copy.Direction = ParameterDirection.Input;
            }
            var plan = new OracleCommandPlan(new ParameterBindingPlan(original.CommandText, inputs), outputs.ToArray(), complexTypes.ToArray());
            if (arrayCount != 0)
            {
                if (inputs.Length == 0) throw new ArgumentException("Array binding requires parameter columns.");
                var outputArrays = new OceanBaseComplexType[complexTypes.Count];
                for (var i = 0; i < outputArrays.Length; i++) outputArrays[i] = complexTypes[i]!;
                plan.ArrayBinding = new OracleArrayBindingPlan(arrayCount, pl, outputArrays);
            }
            return plan;
        }

        private static bool IsArrayPl(string text)
        {
            var start = 0;
            while (start < text.Length)
            {
                if (char.IsWhiteSpace(text[start])) { start++; continue; }
                if (start + 1 < text.Length && text[start] == '-' && text[start + 1] == '-')
                { while (start < text.Length && text[start] != '\n' && text[start] != '\r') start++; continue; }
                if (start + 1 < text.Length && text[start] == '/' && text[start + 1] == '*')
                {
                    var end = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
                    if (end < 0) throw new ArgumentException("Unterminated SQL comment.");
                    start = end + 2; continue;
                }
                break;
            }
            var position = start; while (position < text.Length && IsPart(text[position])) position++;
            var keyword = text.Substring(start, position - start).ToUpperInvariant();
            if (keyword == "BEGIN" || keyword == "DECLARE" || keyword == "CALL") return true;
            if (keyword == "INSERT" || keyword == "UPDATE" || keyword == "DELETE" || keyword == "MERGE") return false;
            throw new NotSupportedException("Array binding supports Oracle DML and PL statements.");
        }

        private static string BindingName(string name) => name.Length > 0 && name[0] == ':' ? name.Substring(1) : name;

        private static void ValidateProcedureName(string text)
        {
            var position = 0;
            while (position < text.Length)
            {
                if (text[position] == '"')
                {
                    var start = ++position;
                    var closed = false;
                    while (position < text.Length)
                    {
                        var c = text[position++];
                        if (char.IsControl(c)) throw InvalidName();
                        if (c != '"') continue;
                        if (position < text.Length && text[position] == '"') { position++; continue; }
                        if (position == start + 1) throw InvalidName();
                        closed = true; break;
                    }
                    if (!closed) throw InvalidName();
                }
                else
                {
                    if (!IsStart(text[position++])) throw InvalidName();
                    while (position < text.Length && IsPart(text[position])) position++;
                }
                if (position == text.Length) return;
                if (text[position++] != '.' || position == text.Length) throw InvalidName();
            }
            throw InvalidName();
        }

        private static bool IsStart(char c) => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '_';
        private static bool IsPart(char c) => IsStart(c) || c >= '0' && c <= '9' || c == '$' || c == '#';
        private static ArgumentException InvalidName() => new ArgumentException("StoredProcedure requires a qualified Oracle identifier.");
    }
}
