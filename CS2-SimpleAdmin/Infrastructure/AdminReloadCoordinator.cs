namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>How a (possibly coalesced) admin reload ended. Never conflated: a failure is not a success.</summary>
internal enum AdminReloadResult
{
    /// <summary>Groups and admins were read, written and applied.</summary>
    Success,

    /// <summary>
    /// The reload could not be completed (database read, file write, queue full…). The previously applied permissions
    /// are untouched. Startup must not treat this as ready; a manual command reports it.
    /// </summary>
    Failed,

    /// <summary>The plugin lifetime ended before the reload finished; nothing was applied.</summary>
    Canceled
}

/// <summary>
/// Coalesces concurrent admin reload requests: while one reload runs, every new request shares a single follow-up
/// reload, so N requests cause at most 2 reloads and never queue N file writes. Each request gets the outcome of the
/// reload that actually served it.
/// <para>
/// Guarantees: every returned task completes (success, failure or cancellation); the busy flag is released on every
/// path, including exceptions thrown by the error callback itself; a request made by a newer runtime context is never
/// served by a reload that is bound to an older one.
/// </para>
/// </summary>
internal sealed class AdminReloadCoordinator(Func<RuntimeContext, Task> reloadOnce, Action<Exception>? onError = null)
{
    private readonly object _gate = new();
    private bool _busy;
    private TaskCompletionSource<AdminReloadResult>? _pending;
    private RuntimeContext? _pendingContext;

    public long Coalesced;

    public bool IsBusy
    {
        get { lock (_gate) return _busy; }
    }

    public Task<AdminReloadResult> RequestAsync(RuntimeContext context)
    {
        if (!context.IsCurrent) return Task.FromResult(AdminReloadResult.Canceled);

        lock (_gate)
        {
            if (_busy)
            {
                Interlocked.Increment(ref Coalesced);
                Interlocked.Increment(ref PluginMetrics.ReloadAdminsCoalesced);
                _pending ??= new TaskCompletionSource<AdminReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingContext = context; // the follow-up runs for the newest requester
                return _pending.Task;
            }

            _busy = true;
        }

        var first = new TaskCompletionSource<AdminReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => LoopAsync(first, context));
        return first.Task;
    }

    private async Task LoopAsync(TaskCompletionSource<AdminReloadResult>? current, RuntimeContext context)
    {
        try
        {
            while (current != null)
            {
                AdminReloadResult result;
                try
                {
                    await reloadOnce(context).ConfigureAwait(false);
                    result = AdminReloadResult.Success;
                }
                catch (OperationCanceledException)
                {
                    result = AdminReloadResult.Canceled;
                }
                catch (Exception ex)
                {
                    result = AdminReloadResult.Failed;
                    try
                    {
                        onError?.Invoke(ex);
                    }
                    catch
                    {
                        // a logging failure must not leave the gate busy
                    }
                }

                current.TrySetResult(result);
                lock (_gate)
                {
                    current = _pending;
                    context = _pendingContext ?? context;
                    _pending = null;
                    _pendingContext = null;
                    if (current == null) _busy = false;
                }
            }
        }
        catch (Exception)
        {
            // Defensive: nothing above should throw, but a stuck busy flag would block every future reload
            TaskCompletionSource<AdminReloadResult>? stranded;
            lock (_gate)
            {
                stranded = _pending;
                _pending = null;
                _pendingContext = null;
                _busy = false;
            }

            current?.TrySetResult(AdminReloadResult.Failed);
            stranded?.TrySetResult(AdminReloadResult.Failed);
        }
    }
}
