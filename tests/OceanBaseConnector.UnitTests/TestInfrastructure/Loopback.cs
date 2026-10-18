using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    /// <summary>
    /// Loopback TCP server running a scripted conversation for handshake
    /// tests without a real OceanBase endpoint.
    /// </summary>
    public sealed class LoopbackServer
    {
        private readonly TcpListener _listener;

        private LoopbackServer(TcpListener listener, Task<Task> scriptTask)
        {
            _listener = listener;
            Completion = scriptTask.Unwrap();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public Task Completion { get; }

        public static LoopbackServer Start(Action<TcpClient> script)
        {
            return StartCore(async client => { script(client); await Task.CompletedTask; });
        }

        public static LoopbackServer Start(Func<TcpClient, Task> script)
        {
            return StartCore(script);
        }

        private static LoopbackServer StartCore(Func<TcpClient, Task> script)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var scriptTask = Task.Factory.StartNew(
                async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await script(client);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return new LoopbackServer(listener, scriptTask);
        }

        public void Stop()
        {
            _listener.Stop();
        }
    }

    public static class LoopbackStreamExtensions
    {
        /// <summary>
        /// Reads up to count bytes; connection close or reset (peer disposal
        /// can surface as a reset) counts as end of data.
        /// </summary>
        public static async Task<int> ReadUpToAsync(this Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, total, count - total);
                }
                catch (IOException)
                {
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }
    }
}
