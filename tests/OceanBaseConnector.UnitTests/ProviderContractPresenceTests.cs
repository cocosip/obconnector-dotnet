using System.Data.Common;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ProviderContractPresenceTests
    {
        [Theory]
        [InlineData("OceanBaseConnection", typeof(DbConnection))]
        [InlineData("OceanBaseCommand", typeof(DbCommand))]
        [InlineData("OceanBaseParameter", typeof(DbParameter))]
        [InlineData("OceanBaseDataSource", typeof(DbDataSource))]
        public void PublicTypesImplementAdoNet(string name, System.Type contract)
        {
            var type = typeof(OceanBaseConnectionStringBuilder).Assembly.GetType("OceanBaseConnector." + name);
            Assert.NotNull(type);
            Assert.True(contract.IsAssignableFrom(type));
        }
    }
}
