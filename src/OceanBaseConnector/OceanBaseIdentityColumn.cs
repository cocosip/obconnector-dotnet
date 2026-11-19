namespace OceanBaseConnector
{
    /// <summary>An identity column declared in an Oracle table's DDL.</summary>
    public sealed class OceanBaseIdentityColumn
    {
        internal OceanBaseIdentityColumn(string columnName, string generationType, string sequenceOptions)
        {
            ColumnName = columnName;
            GenerationType = generationType;
            SequenceOptions = sequenceOptions;
        }

        public string ColumnName { get; }
        /// <summary>ALWAYS, BY DEFAULT, or BY DEFAULT ON NULL.</summary>
        public string GenerationType { get; }
        /// <summary>The sequence option clauses emitted by the server, or an empty string.</summary>
        public string SequenceOptions { get; }
    }
}
