using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlipSwitcher.Services;

/// <summary>
/// Keeps blocked Win32 activations bounded and off the shared thread pool.
/// </summary>
internal sealed class WindowActivationService
{
    internal const int MaxConcurrentActivations = 4;
    private readonly object _gate = new();
    private readonly HashSet<IntPtr> _activeHandles = new();

    // A null result means duplicate or at capacity. Do not queue stale focus requests,
    // or release a handle on a timeout: the native call could still be running.
    internal Task? TryActivate(IntPtr handle, Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        lock (_gate)
        {
            if (_activeHandles.Count >= MaxConcurrentActivations || !_activeHandles.Add(handle))
            {
                return null;
            }
        }

        try
        {
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    activate();
                }
                finally
                {
                    Release(handle);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            // Scheduling failed before ownership could pass to the worker.
            Release(handle);
            throw;
        }
    }

    private void Release(IntPtr handle)
    {
        lock (_gate)
        {
            _activeHandles.Remove(handle);
        }
    }
}
