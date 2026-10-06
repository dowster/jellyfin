using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Server.Implementations.Trickplay;

/// <summary>
/// Limits generation across scheduled tasks and library refreshes without replacing live locks.
/// </summary>
internal sealed class TrickplayJobLimiter(Func<int> getLimit)
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;

    public async ValueTask<IDisposable> LockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_lock)
            {
                if (_active < getLimit())
                {
                    _active++;
                    return new Lease(this);
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            _active--;
            var changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.SetResult();
        }
    }

    private sealed class Lease(TrickplayJobLimiter owner) : IDisposable
    {
        private TrickplayJobLimiter? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
