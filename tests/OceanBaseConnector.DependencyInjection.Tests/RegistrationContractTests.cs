using System.Reflection;
using Xunit;

namespace OceanBaseConnector.DependencyInjection.Tests
{
    public sealed class RegistrationContractTests
    {
        [Fact]
        public void IntegrationAssemblyProvidesUnifiedRegistration()
        {
            var assembly = Assembly.Load("OceanBaseConnector.DependencyInjection");
            Assert.NotNull(assembly.GetType("Microsoft.Extensions.DependencyInjection.OceanBaseServiceCollectionExtensions"));
        }
    }
}
