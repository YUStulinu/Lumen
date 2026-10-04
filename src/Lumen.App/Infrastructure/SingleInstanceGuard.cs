namespace Lumen.App.Infrastructure;

/// <summary>
/// Makes sure only one Lumen runs per user session; a second launch wakes up the first one.
/// </summary>
/// <remarks>
/// <para>A named <see cref="Mutex"/> is a kernel object visible to every process in the session
/// ("Local\" prefix). The first instance creates it; later instances see it already exists.</para>
/// <para>A named <see cref="EventWaitHandle"/> is the doorbell: the second instance signals it and
/// exits, and the first instance, which waits on it from a background thread, opens its window.
/// Two kernel objects, no sockets or pipes needed.</para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\Lumen.SingleInstance.7F3A";
    private const string EventName = @"Local\Lumen.Activate.7F3A";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private RegisteredWaitHandle? _registration;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public bool IsFirstInstance { get; }

    /// <summary>Called by a second instance: asks the first one to show itself.</summary>
    public void SignalFirstInstance() => _activateEvent.Set();

    /// <summary>Called by the first instance: <paramref name="onActivate"/> runs (on a pool thread) whenever a later launch rings.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _activateEvent.Dispose();
    }
}
