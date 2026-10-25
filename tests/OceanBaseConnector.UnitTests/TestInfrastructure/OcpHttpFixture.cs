using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal sealed class OcpHttpFixture : HttpMessageHandler
    {
        internal OcpHttpFixture(string body) { Body = body; Client = new HttpClient(this, disposeHandler: false); }
        internal HttpClient Client { get; }
        internal string Body { get; set; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal ConcurrentQueue<Uri> Requests { get; } = new ConcurrentQueue<Uri>();
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            if (Respond != null) return Respond(request, cancellationToken);
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                RequestMessage = request,
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
        }

        protected override void Dispose(bool disposing) { if (disposing) Client.Dispose(); base.Dispose(disposing); }
        internal static string Entry(int port, string host = "127.0.0.1") =>
            "{\"successful\":true,\"status\":200,\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"ADDRESS_LIST\":[{\"ADDRESS\":[{\"PROTOCOL\":\"TCP\",\"HOST\":\"" + host + "\",\"PORT\":" + port + "}]}]}}}";
    }
}
