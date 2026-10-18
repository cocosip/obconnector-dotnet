using System;
using System.IO;

namespace OceanBaseConnector.ConsumerTests
{
    /// <summary>
    /// Locates the repository root and product build outputs from a test host.
    /// Test binaries run under tests/&lt;project&gt;/bin/&lt;config&gt;/&lt;tfm&gt;, so the
    /// repository root is a fixed number of levels above the base directory.
    /// </summary>
    public static class RepositoryLocator
    {
        public static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && directory is not null; i++)
            {
                if (directory.GetFiles("OceanBaseConnector.slnx").Length > 0)
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"Could not locate OceanBaseConnector.slnx above '{AppContext.BaseDirectory}'.");
        }

        public static string GetProductAssemblyPath(string targetFramework, string configuration = "Release")
        {
            var path = Path.Combine(
                FindRoot(),
                "src", "OceanBaseConnector", "bin", configuration, targetFramework, "OceanBaseConnector.dll");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Product asset not found at '{path}'. Build the solution in {configuration} first.", path);
            }

            return path;
        }
    }
}
