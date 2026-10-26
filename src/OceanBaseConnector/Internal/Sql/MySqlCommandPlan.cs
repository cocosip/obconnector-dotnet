using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector.Internal.Sql
{
    internal sealed class MySqlCommandPlan
    {
        private MySqlCommandPlan(ParameterBindingPlan bindings, OceanBaseParameter[] outputs, ParameterBindingPlan initialization, string outputSql)
        { Bindings = bindings; Outputs = outputs; _initialization = initialization; OutputSql = outputSql; }

        internal ParameterBindingPlan Bindings { get; }
        internal OceanBaseParameter[] Outputs { get; }
        internal string OutputSql { get; }
        private readonly ParameterBindingPlan _initialization;
        private object[]? _values;

        internal static MySqlCommandPlan Create(string name, OceanBaseParameterCollection parameters, string variablePrefix, Encoding? clientEncoding = null)
        {
            var parts = RoutineParameterDerivation.ParseName(name, mysql: true);
            if (parameters.Count > ushort.MaxValue) throw new ArgumentException("Too many routine parameters.");
            var sql = new StringBuilder("CALL ");
            for (var i = 0; i < parts.Length; i++)
            { if (i != 0) sql.Append('.'); sql.Append('`').Append(parts[i].Replace("`", "``")).Append('`'); }
            sql.Append('(');
            var inputs = new List<OceanBaseParameter>(); var outputs = new List<OceanBaseParameter>();
            var initializers = new List<OceanBaseParameter>(); var assignments = new List<string>(); var variables = new List<string>();
            foreach (OceanBaseParameter parameter in parameters)
            {
                if (parameter.Direction == ParameterDirection.ReturnValue)
                    throw new NotSupportedException("MySQL procedures do not have a ReturnValue; call stored functions with a text SELECT.");
                if (inputs.Count + outputs.Count != 0) sql.Append(", ");
                var copy = parameter.Snapshot(); copy.Direction = ParameterDirection.Input;
                if (parameter.Direction == ParameterDirection.Input) { sql.Append('?'); inputs.Add(copy); continue; }
                if (parameter.Direction != ParameterDirection.Output && parameter.Direction != ParameterDirection.InputOutput)
                    throw new ArgumentException("Invalid routine parameter direction.");
                if (parameter.Direction == ParameterDirection.InputOutput && copy.Value is Stream)
                    throw new NotSupportedException("Stream parameters require Input direction.");
                if (outputs.Count == 4096) throw new NotSupportedException("Too many routine output parameters.");
                var variable = "@" + variablePrefix + "_" + outputs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                sql.Append(variable); variables.Add(variable); assignments.Add(variable + " = ?"); outputs.Add(parameter);
                if (parameter.Direction == ParameterDirection.Output) copy.Value = DBNull.Value;
                initializers.Add(copy);
            }
            var initialization = new ParameterBindingPlan("SET " + string.Join(", ", assignments), initializers.ToArray());
            // Validate INOUT values before any initialization or CALL can be transmitted.
            if (initializers.Count != 0) PreparedParameters.Encode(initialization.Parameters, OceanBaseCompatibilityMode.MySql, validateOnly: true, clientEncoding: clientEncoding);
            return new MySqlCommandPlan(new ParameterBindingPlan(sql.Append(')').ToString(), inputs.ToArray()), outputs.ToArray(),
                initialization, "SELECT " + string.Join(", ", variables));
        }

        internal void Initialize(CommandExchange exchange, PhysicalSession session)
        {
            if (Outputs.Length == 0) return;
            var encoded = PreparedParameters.Encode(_initialization.Parameters, OceanBaseCompatibilityMode.MySql, clientEncoding: exchange.ClientEncoding);
            exchange.Send(OceanBaseCommand.SqlPacket(_initialization.CommandText, 22, exchange.ClientEncoding), executesSql: false);
            var statement = PreparedStatement.Read(exchange, session, _initialization.CommandText)!;
            var closed = false;
            try
            {
                ValidateInitialization(statement);
                exchange.Send(PreparedParameters.Execute(statement.Id, encoded));
                FinishInitialization(exchange, exchange.Receive());
                exchange.Send(PreparedStatement.ClosePacket(statement.Id), executesSql: false);
                exchange.ResponseComplete(expectsResponse: false); closed = true;
            }
            finally { if (!closed) statement.Release(); }
        }

        internal async Task InitializeAsync(CommandExchange exchange, PhysicalSession session, CancellationToken token)
        {
            if (Outputs.Length == 0) return;
            var encoded = PreparedParameters.Encode(_initialization.Parameters, OceanBaseCompatibilityMode.MySql, clientEncoding: exchange.ClientEncoding);
            await exchange.SendAsync(OceanBaseCommand.SqlPacket(_initialization.CommandText, 22, exchange.ClientEncoding), executesSql: false).ConfigureAwait(false);
            var statement = (await PreparedStatement.ReadAsync(exchange, session, _initialization.CommandText, token).ConfigureAwait(false))!;
            var closed = false;
            try
            {
                ValidateInitialization(statement);
                await exchange.SendAsync(PreparedParameters.Execute(statement.Id, encoded)).ConfigureAwait(false);
                FinishInitialization(exchange, await exchange.ReceiveAsync(token).ConfigureAwait(false));
                await exchange.SendAsync(PreparedStatement.ClosePacket(statement.Id), executesSql: false).ConfigureAwait(false);
                exchange.ResponseComplete(expectsResponse: false); closed = true;
            }
            finally { if (!closed) statement.Release(); }
        }

        private void ValidateInitialization(PreparedStatement statement)
        {
            if (statement.ParameterCount != Outputs.Length || statement.ColumnCount != 0)
                throw new ProtocolException("Routine initialization metadata does not match the request.");
        }

        private static void FinishInitialization(CommandExchange exchange, byte[] packet)
        {
            var ok = OkPacket.Parse(packet, exchange.Capabilities);
            if ((ok.StatusFlags & ServerStatusFlags.MoreResultsExists) != 0)
                throw new ProtocolException("Routine initialization returned extra results.");
            exchange.ObserveSessionState(ok); exchange.ResponseComplete();
        }

        internal void CaptureOutputs(QueryResult result, bool hasRow)
        {
            if (!hasRow || result.Columns.Length != Outputs.Length)
                throw new ProtocolException("Routine output metadata does not match the bound parameters.");
            var values = new object[Outputs.Length];
            for (var i = 0; i < values.Length; i++) values[i] = result.Columns[i].Decode(result.Row![i]);
            _values = values;
        }

        internal void PublishOutputs()
        {
            if (Outputs.Length == 0) return;
            if (_values == null) throw new ProtocolException("Routine execution completed without its bound output values.");
            for (var i = 0; i < _values.Length; i++) Outputs[i].Value = _values[i];
        }
    }
}
