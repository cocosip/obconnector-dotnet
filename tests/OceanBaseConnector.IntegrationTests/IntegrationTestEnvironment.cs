using System;
using System.Collections.Generic;
using Xunit;

namespace OceanBaseConnector.IntegrationTests
{
    /// <summary>
    /// Runs the test only when an integration environment is explicitly
    /// requested. With OB_REQUIRE_INTEGRATION unset the test is skipped with
    /// a visible reason; with OB_REQUIRE_INTEGRATION=1 but missing connection
    /// strings the test body fails, so gates cannot pass on a half-configured
    /// environment (development plan: "关口验收设置 OB_REQUIRE_INTEGRATION=1,
    /// 缺少必需环境时测试失败,日常开发可明确跳过并报告").
    /// </summary>
    public sealed class IntegrationFactAttribute : FactAttribute
    {
        public const string RequireIntegrationVariable = "OB_REQUIRE_INTEGRATION";

        public static readonly IReadOnlyList<string> RequiredConnectionStrings = new[]
        {
            "OB_MYSQL_CONNECTION_STRING",
            "OB_ORACLE_CONNECTION_STRING",
            "OB_MYSQL_PROXY_CONNECTION_STRING",
            "OB_ORACLE_PROXY_CONNECTION_STRING",
        };

        public IntegrationFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(RequireIntegrationVariable) != "1")
            {
                Skip = $"Integration environment not requested; set {RequireIntegrationVariable}=1 plus the OB_*_CONNECTION_STRING variables to run.";
            }
        }
    }

    public static class IntegrationTestEnvironment
    {
        /// <summary>
        /// Returns the connection strings required by the gate tests. Throws
        /// with the list of missing variables so a forced gate run fails
        /// loudly instead of silently passing.
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetRequiredConnectionStrings()
        {
            var missing = new List<string>();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in IntegrationFactAttribute.RequiredConnectionStrings)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(value))
                {
                    missing.Add(name);
                }
                else
                {
                    values[name] = value!;
                }
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"OB_REQUIRE_INTEGRATION=1 but missing: {string.Join(", ", missing)}. A gate run must not pass on an unconfigured environment.");
            }

            return values;
        }
    }
}
