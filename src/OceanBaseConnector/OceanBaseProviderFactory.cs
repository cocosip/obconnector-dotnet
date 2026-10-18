using System;
using System.Data.Common;

namespace OceanBaseConnector
{
    public sealed class OceanBaseProviderFactory : DbProviderFactory
    {
        public static readonly OceanBaseProviderFactory Instance = new OceanBaseProviderFactory();
        private OceanBaseProviderFactory() { }
#if NET8_0_OR_GREATER
        public override bool CanCreateDataAdapter => false;
        public override bool CanCreateCommandBuilder => false;
#endif
        public override DbConnection CreateConnection() => new OceanBaseConnection();
        public override DbCommand CreateCommand() => new OceanBaseCommand();
        public override DbParameter CreateParameter() => new OceanBaseParameter();
        public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new OceanBaseConnectionStringBuilder();
        public override DbDataAdapter? CreateDataAdapter() => null;
        public override DbCommandBuilder? CreateCommandBuilder() => null;
#if NET8_0_OR_GREATER
        public override DbDataSource CreateDataSource(string connectionString) => new OceanBaseDataSourceBuilder(connectionString).Build();
#endif
    }
}
