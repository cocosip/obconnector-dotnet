using System;
using System.Data.Common;

namespace OceanBaseConnector
{
    /// <summary>Fills tables and applies row changes with ordinary synchronous commands.</summary>
    public sealed class OceanBaseDataAdapter : DbDataAdapter
    {
        public OceanBaseDataAdapter() { }
        public OceanBaseDataAdapter(OceanBaseCommand selectCommand) => SelectCommand = selectCommand;
        public OceanBaseDataAdapter(string selectCommandText, OceanBaseConnection connection)
            => SelectCommand = new OceanBaseCommand { CommandText = selectCommandText, Connection = connection };
        public OceanBaseDataAdapter(string selectCommandText, string connectionString)
            : this(selectCommandText, new OceanBaseConnection(connectionString)) { }

        public new OceanBaseCommand? SelectCommand { get => (OceanBaseCommand?)base.SelectCommand; set => base.SelectCommand = value; }
        public new OceanBaseCommand? InsertCommand { get => (OceanBaseCommand?)base.InsertCommand; set => base.InsertCommand = value; }
        public new OceanBaseCommand? UpdateCommand { get => (OceanBaseCommand?)base.UpdateCommand; set => base.UpdateCommand = value; }
        public new OceanBaseCommand? DeleteCommand { get => (OceanBaseCommand?)base.DeleteCommand; set => base.DeleteCommand = value; }

        public event EventHandler<RowUpdatingEventArgs>? RowUpdating;
        public event EventHandler<RowUpdatedEventArgs>? RowUpdated;
        protected override void OnRowUpdating(RowUpdatingEventArgs value)
        {
            base.OnRowUpdating(value);
            RowUpdating?.Invoke(this, value);
        }
        protected override void OnRowUpdated(RowUpdatedEventArgs value)
        {
            base.OnRowUpdated(value);
            RowUpdated?.Invoke(this, value);
        }
    }
}
