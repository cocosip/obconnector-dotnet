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
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, "tns-" + Guid.NewGuid().ToString("N") + ".ob");
            Write(content);
        }

        internal string Path { get; }
        internal void Write(string content) => File.WriteAllText(Path, content, new UTF8Encoding(false));
        internal static string Entry(int port) => "APP=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + port.ToString(CultureInfo.InvariantCulture) + "))))";
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
