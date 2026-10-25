using System;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed class CloseCallbackScope : IDisposable
    {
        private static readonly AsyncLocal<CloseCallbackScope?> Current = new AsyncLocal<CloseCallbackScope?>();
        private readonly CloseCallbackScope? _previous = Current.Value;
        private Action? _cleanup;
        private Func<Task>? _cleanupAsync;
        private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        internal CloseCallbackScope(Action cleanup, Func<Task> cleanupAsync)
        { _cleanup = cleanup; _cleanupAsync = cleanupAsync; }

        internal IDisposable Enter() { Current.Value = this; return this; }
        public void Dispose() => Current.Value = _previous;

        internal static void CompletePending()
        {
            // A callback can close another connection before reopening the outer pool.
            for (var scope = Current.Value; scope != null; scope = scope._previous) scope.Complete();
        }

        internal static async Task CompletePendingAsync()
        {
            for (var scope = Current.Value; scope != null; scope = scope._previous)
                await scope.CompleteAsync().ConfigureAwait(false);
        }

        internal void Complete()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                var cleanup = _cleanup!;
                _cleanup = null;
                _cleanupAsync = null;
                var previous = Current.Value;
                Current.Value = null;
                try { cleanup(); _completion.TrySetResult(true); }
                catch (Exception error) { _completion.TrySetException(error); }
                finally { Current.Value = previous; }
            }
            _completion.Task.GetAwaiter().GetResult();
        }

        internal async Task CompleteAsync()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                Func<Task>? cleanup = _cleanupAsync!;
                _cleanup = null;
                _cleanupAsync = null;
                var previous = Current.Value;
                Current.Value = null;
                Exception? failure = null;
                try { await cleanup().ConfigureAwait(false); }
                catch (Exception error) { failure = error; }
                // The lifted delegate must be released before other completions can resume.
                finally { cleanup = null; Current.Value = previous; }
                if (failure == null) _completion.TrySetResult(true);
                else _completion.TrySetException(failure);
            }
            await _completion.Task.ConfigureAwait(false);
        }
    }
}
