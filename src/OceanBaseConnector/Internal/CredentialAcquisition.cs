using System;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal
{
    internal static class CredentialAcquisition
    {
        internal static OceanBaseConnectionOptions Resolve(OceanBaseConnectionOptions options, IOceanBaseCredentialProvider? provider, CancellationToken token)
        {
            if (provider == null) return options;
            token.ThrowIfCancellationRequested();
            OceanBaseCredential? credential;
            try { credential = provider.GetCredential(new OceanBaseCredentialContext(options), token); }
            catch { token.ThrowIfCancellationRequested(); throw Failed(); }
            return Apply(options, credential, token);
        }

        internal static async Task<OceanBaseConnectionOptions> ResolveAsync(OceanBaseConnectionOptions options, IOceanBaseCredentialProvider? provider, CancellationToken token)
        {
            if (provider == null) return options;
            token.ThrowIfCancellationRequested();
            OceanBaseCredential? credential;
            try
            {
                var pending = provider.GetCredentialAsync(new OceanBaseCredentialContext(options), token) ?? throw Failed();
                var canceled = new TaskCompletionSource<OceanBaseCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => canceled.TrySetCanceled()))
                {
                    var completed = await Task.WhenAny(pending, canceled.Task).ConfigureAwait(false);
                    if (!ReferenceEquals(completed, pending))
                        _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    credential = await completed.ConfigureAwait(false);
                }
            }
            catch { token.ThrowIfCancellationRequested(); throw Failed(); }
            return Apply(options, credential, token);
        }

        private static OceanBaseConnectionOptions Apply(OceanBaseConnectionOptions options, OceanBaseCredential? credential, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (credential == null) throw Failed();
            credential.EnsureValid();
            return options.WithCredential(credential);
        }
        // Provider exceptions can contain credentials, including in their inner exceptions.
        private static OceanBaseException Failed() => new OceanBaseException("The credential provider failed to supply a credential.");
    }
}
