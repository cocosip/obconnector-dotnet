using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Transport
{
    /// <summary>
    /// Owns the TCP socket for one physical session. Connect honors the
    /// connection timeout and caller cancellation on both the synchronous
    /// and asynchronous paths without one wrapping the other; failure and
    /// cancellation always dispose the socket so no half-open connection
    /// survives. Endpoint information in exceptions is safe to expose;
    /// credentials never reach this class.
    /// </summary>
    internal sealed class TcpTransport : IDisposable
    {
        private readonly TcpClient _client;

        private TcpTransport(TcpClient client)
        {
            _client = client;
        }

        public Stream Stream => _client.GetStream();

        public static TcpTransport Connect(string host, int port, TimeSpan timeout)
        {
            var client = new TcpClient();
            try
            {
                var asyncResult = client.BeginConnect(host, port, null, null);
                if (!asyncResult.AsyncWaitHandle.WaitOne(timeout))
                {
                    client.Close();
                    throw CreateTimeoutException(host, port, timeout);
                }

                try
                {
                    client.EndConnect(asyncResult);
                }
                catch (SocketException exception)
                {
                    throw CreateConnectFailureException(host, port, exception);
                }

                return new TcpTransport(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static async Task<TcpTransport> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var client = new TcpClient();
            try
            {
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                combinedCts.CancelAfter(timeout);

#if !NETSTANDARD2_0
                try
                {
                    await client.ConnectAsync(host, port, combinedCts.Token).ConfigureAwait(false);
                    return new TcpTransport(client);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw CreateTimeoutException(host, port, timeout);
                }
                catch (SocketException exception)
                {
                    throw CreateConnectFailureException(host, port, exception);
                }
#else
                var connectTask = client.ConnectAsync(host, port);
                // netstandard2.0 has no cancellable ConnectAsync: dispose the
                // socket to abort a pending connect when cancellation fires.
                using (combinedCts.Token.Register(state => ((TcpClient)state!).Dispose(), client))
                {
                    try
                    {
                        await connectTask.ConfigureAwait(false);
                        return new TcpTransport(client);
                    }
                    catch (Exception exception)
                        when (!cancellationToken.IsCancellationRequested &&
                             (exception is ObjectDisposedException || exception is SocketException))
                    {
                        if (combinedCts.IsCancellationRequested)
                        {
                            throw CreateTimeoutException(host, port, timeout);
                        }

                        if (exception is SocketException socketException)
                        {
                            throw CreateConnectFailureException(host, port, socketException);
                        }

                        throw;
                    }
                }
#endif
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public void Close()
        {
            _client.Close();
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private static TimeoutException CreateTimeoutException(string host, int port, TimeSpan timeout)
        {
            return new TimeoutException(
                $"Connect to {host}:{port} timed out after {(int)timeout.TotalMilliseconds} ms.");
        }

        private static IOException CreateConnectFailureException(string host, int port, SocketException exception)
        {
            return new IOException(
                $"Connect to {host}:{port} failed ({exception.SocketErrorCode}): {exception.Message}",
                exception);
        }
    }
}
