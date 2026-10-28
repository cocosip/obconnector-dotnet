using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class CredentialProviderTests
    {

        private static async Task Expire(DateTimeOffset expires)
        {
            while (DateTimeOffset.UtcNow <= expires) await Task.Delay(20);
        }
    }
}
