#if NET8_0_OR_GREATER
using System;
using System.Data;
using System.Data.Common;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed class OceanBaseBatchCommand : DbBatchCommand
    {
        private readonly OceanBaseParameterCollection _parameters = new OceanBaseParameterCollection();
        private OceanBaseCommand? _command;
        private string _text = string.Empty;
        private CommandType _type = CommandType.Text;
        internal OceanBaseBatch? Owner { get; set; }
        public override string CommandText { get => _text; set { Owner?.CheckMutable(); _text = value; } }
        public override CommandType CommandType { get => _type; set { Owner?.CheckMutable(); _type = value; } }
        public override int RecordsAffected => Affected;
        internal int Affected { get; set; } = -1;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        public override bool CanCreateParameter => true;
        public override DbParameter CreateParameter() => new OceanBaseParameter();

        internal OceanBaseCommand Snapshot(OceanBaseConnection connection, OceanBaseTransaction? transaction, int timeout, OceanBaseBatch.Operation owner, PhysicalSession session)
        {
            _command ??= new OceanBaseCommand();
            _command.Connection = connection; _command.Transaction = transaction; _command.CommandTimeout = timeout;
            _command.CommandText = _text; _command.CommandType = _type;
            _command.SessionOwner = owner; _command.RequiredSession = session; _command.BatchCommand = this;
            _command.CancellationOrigin = owner.CancellationOrigin; _command.BatchFailed = owner.Fail;
            _command.Parameters.Clear();
            foreach (OceanBaseParameter parameter in _parameters) _command.Parameters.Add(parameter.Snapshot());
            return _command;
        }

        internal void Release() { var command = _command; _command = null; command?.Dispose(); }
        internal async System.Threading.Tasks.ValueTask ReleaseAsync()
        { var command = _command; _command = null; if (command != null) await command.DisposeAsync().ConfigureAwait(false); }
    }
}
#endif
