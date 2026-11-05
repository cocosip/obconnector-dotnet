using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal sealed class TnsFileFixture : IDisposable
    {
        internal TnsFileFixture(string content)
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "obconnector-tns-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "addresses.ob");
            Write(content);
        }

        private string Directory { get; }
        internal string Path { get; }
        internal void Write(string content) => File.WriteAllText(Path, content, new UTF8Encoding(false));
        internal static string Entry(int port) => "APP=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + port.ToString(CultureInfo.InvariantCulture) + "))))";
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
