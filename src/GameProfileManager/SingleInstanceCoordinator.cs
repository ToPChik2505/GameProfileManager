using System.Security.AccessControl;
using System.Security.Principal;

namespace GameProfileManager;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _mutexName;
    private readonly string _activationName;
    private readonly SecurityIdentifier _user;
    private Mutex? _mutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _registration;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator(string identity = "GameProfileManager")
    {
        _user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");
        string scope = $"Local\\{identity}.{_user.Value}";
        _mutexName = scope + ".Instance";
        _activationName = scope + ".Activate";
    }

    public bool TrySignalExisting(bool activate = true)
    {
        try
        {
            if (!EventWaitHandleAcl.TryOpenExisting(_activationName, EventWaitHandleRights.Modify,
                    out EventWaitHandle? activation)) return false;
            using (activation) { if (activate) activation.Set(); }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public bool TryStart(Action onActivation, bool activateExisting = true)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SingleInstanceCoordinator));
        if (_mutex is not null) throw new InvalidOperationException("Проверка экземпляра уже запущена.");

        var mutexSecurity = new MutexSecurity();
        mutexSecurity.AddAccessRule(new MutexAccessRule(_user, MutexRights.FullControl,
            AccessControlType.Allow));
        _mutex = MutexAcl.Create(false, _mutexName, out _, mutexSecurity);
        try
        {
            try { _ownsMutex = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            if (!_ownsMutex)
            {
                _mutex.Dispose();
                _mutex = null;
                TrySignalExisting(activateExisting);
                return false;
            }

            var eventSecurity = new EventWaitHandleSecurity();
            eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(_user,
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));
            _activation = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset,
                _activationName, out _, eventSecurity);
            _registration = ThreadPool.RegisterWaitForSingleObject(_activation,
                (_, timedOut) =>
                {
                    if (!timedOut && !_disposed) onActivation();
                }, null, Timeout.Infinite, false);
            return true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration?.Unregister(null);
        _activation?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _registration = null;
        _activation = null;
        _mutex = null;
        _ownsMutex = false;
    }
}
