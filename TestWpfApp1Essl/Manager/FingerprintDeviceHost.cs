using Axzkemkeeper;
using Pasumai.Common;
using Pasumai.Common.Model;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Passoft.Manager
{
    // Single owner of the ZKTeco AxCZKEM ActiveX control for the whole app.
    //
    // AxCZKEM is an OCX: the control instance is bound to the STA apartment (thread) that
    // created it, every method call and every real-time event (OnEnrollFinger, log events)
    // marshals against that apartment, and events are delivered by PostMessage - so that
    // thread must run a message pump for the control's lifetime. The SDK is also not
    // concurrency-safe: two device operations in flight at once corrupt each other.
    //
    // This host solves all three: one dedicated background STA thread creates the control
    // once, runs Dispatcher.Run() for the app's lifetime (pump), and every device call is
    // queued onto that thread and serialized by a single-slot semaphore. Callers never
    // touch AxCZKEM directly; they go through InvokeAsync.
    //
    // Lifetime: created lazily on first use (only on a local fingerprint-machine branch),
    // shut down from MainWindow on close. On any non-fingerprint machine the host is never
    // created and callers guard on ServerSettings before reaching it.
    sealed class FingerprintDeviceHost : IDisposable
    {
        private static readonly object _instanceLock = new object();
        private static FingerprintDeviceHost _instance;

        // Returns the shared host, creating (and starting) it on first call. Returns null if
        // this machine is not a local fingerprint-machine branch, or if the control could not
        // be created (SDK not registered / DLLs missing) - callers treat null as "device
        // unavailable", same as the old _canStart == false.
        public static FingerprintDeviceHost GetOrCreate(ServerSettings serverSetting)
        {
            if (serverSetting == null
                || !serverSetting.IsServerLocal
                || serverSetting.AttendanceType != Constants.EnumAttendanceType.FingerPrintMachine)
                return null;

            lock (_instanceLock)
            {
                if (_instance != null && _instance._faulted)
                {
                    // A previous start failed (or the thread died). Drop it and try once more -
                    // the SDK may have been registered since, or the device reconnected.
                    _instance.Dispose();
                    _instance = null;
                }
                if (_instance == null)
                {
                    var host = new FingerprintDeviceHost();
                    if (!host.Start())
                    {
                        host.Dispose();
                        return null;
                    }
                    _instance = host;
                }
                return _instance;
            }
        }

        public static void ShutdownShared()
        {
            lock (_instanceLock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);   // serializes every device op
        private Dispatcher _dispatcher;
        private AxCZKEM _device;
        private volatile bool _faulted;
        private bool _disposed;

        private FingerprintDeviceHost()
        {
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "FingerprintDeviceHost"
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        private bool Start()
        {
            _thread.Start();
            _ready.Wait();
            return !_faulted && _device != null;
        }

        private void ThreadMain()
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _device = CreateControl();
                if (_device == null)
                {
                    _faulted = true;
                    _ready.Set();
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("FingerprintDeviceHost: control creation failed", ex);
                _faulted = true;
                _ready.Set();
                return;
            }

            _ready.Set();

            try
            {
                Dispatcher.Run();   // pumps messages until InvokeShutdown() -> real-time events flow
            }
            catch (Exception ex)
            {
                Logger.Log("FingerprintDeviceHost: dispatcher loop crashed", ex);
                _faulted = true;
            }
            finally
            {
                try { _device?.Disconnect(); } catch { }
                try { _device?.Dispose(); } catch { }
                _device = null;
            }
        }

        // Mirrors the old AttendanceSyncManager._initiateConnection(forceStart: true): create the
        // AxCZKEM, copy tcpcomm.dll next to it, and if the class isn't registered, register
        // zkemkeeper.dll and retry once.
        private static AxCZKEM CreateControl()
        {
            var device = new AxCZKEM { AccessibleName = "1" };
            try
            {
                Utilities.CopyDll("tcpcomm.dll");
                device.CreateControl();
                device.SetCommPassword(0);
                return device;
            }
            catch (Exception ex)
            {
                Logger.Log("FingerprintDeviceHost: CreateControl try 1 failed - " + ex);
                if (ex.Message.ContainsIgnoreCase("class not registered")
                    || ex.Message.ContainsIgnoreCase("The specified module could not be found"))
                {
                    try
                    {
                        Utilities.RegisterDll(@"\ref\eTimeTrack\zkemkeeper.dll");
                        device.CreateControl();
                        device.SetCommPassword(0);
                        return device;
                    }
                    catch (Exception ex2)
                    {
                        Logger.Log("FingerprintDeviceHost: CreateControl try 2 failed - " + ex2);
                    }
                }
                try { device.Dispose(); } catch { }
                return null;
            }
        }

        // Queue a device operation onto the host thread. The delegate runs on the STA apartment
        // that owns the control, with exclusive access (no other op runs until it returns). The
        // op is handed the control and a CancellationToken; long waits inside it (e.g. awaiting
        // an OnEnrollFinger TaskCompletionSource) still pump because Dispatcher.Run() is running.
        public async Task<T> InvokeAsync<T>(Func<AxCZKEM, CancellationToken, Task<T>> op, CancellationToken ct = default(CancellationToken))
        {
            if (op == null) throw new ArgumentNullException("op");
            ThrowIfUnusable();

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfUnusable();
                var dispatcherOp = _dispatcher.InvokeAsync(() => op(_device, ct));
                return await dispatcherOp.Task.Unwrap().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<T> InvokeAsync<T>(Func<AxCZKEM, T> op, CancellationToken ct = default(CancellationToken))
        {
            if (op == null) throw new ArgumentNullException("op");
            return InvokeAsync((d, _) => Task.FromResult(op(d)), ct);
        }

        public Task InvokeAsync(Action<AxCZKEM> op, CancellationToken ct = default(CancellationToken))
        {
            if (op == null) throw new ArgumentNullException("op");
            return InvokeAsync<object>((d, _) => { op(d); return Task.FromResult<object>(null); }, ct);
        }

        private void ThrowIfUnusable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FingerprintDeviceHost));
            if (_faulted || _device == null || _dispatcher == null || !_thread.IsAlive)
            {
                _faulted = true;   // let GetOrCreate() rebuild the host on the next call
                throw new InvalidOperationException("Fingerprint device host is not available.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _dispatcher?.InvokeShutdown(); } catch { }
            try
            {
                if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
                    Logger.Log("FingerprintDeviceHost: host thread did not exit within 5s");
            }
            catch { }
            _gate.Dispose();
            _ready.Dispose();
        }
    }
}
