namespace BekkyWake.Core.Store;

/// <summary>
/// Exclusive OS-level file lock on recovery.lock so the live tray app and the
/// 1-minute Task Scheduler backstop never race over state.json or LIDACTION.
/// </summary>
public sealed class RecoveryLock
{
    private readonly AppPaths _paths;

    public RecoveryLock(AppPaths paths)
    {
        _paths = paths;
    }

    public IDisposable? TryAcquire()
    {
        _paths.EnsureDirectories();
        try
        {
            var fs = new FileStream(
                _paths.RecoveryLockFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new LockHandle(fs);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<IDisposable?> AcquireAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            var handle = TryAcquire();
            if (handle is not null)
            {
                return handle;
            }

            if (Environment.TickCount64 >= deadline || ct.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    private sealed class LockHandle(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _stream, null);
            s?.Dispose();
        }
    }
}
