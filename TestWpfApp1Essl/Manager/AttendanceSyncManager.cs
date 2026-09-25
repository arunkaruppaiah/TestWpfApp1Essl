using Axzkemkeeper;
using Pasumai.Common;
using Pasumai.Common.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Passoft.Manager
{
    // Test copy of Passoft\Manager\AttendanceSyncManager.cs. Device code is kept identical to
    // Passoft; only the database calls are replaced (marked "TEST:") because there is no DB here.
    // Changes to port back to Passoft:
    //   - Connect(): comm key via SetCommPasswordEx + error code in the log
    //   - GetDeviceInfo(), PromptUserFaceEnrollment(): new
    class AttendanceSyncManager
    {
        public EventHandler<AttendanceSyncEventArgs> ReportSyncProgress;
        private readonly ServerSettings _serverSetting;
        public string ErrorMessage { get; set; }

        // ZKTeco face terminals store the face template in slot 111.
        private const int FaceIndex = 111;

        public AttendanceSyncManager(ServerSettings serversettings)
        {
            _serverSetting = serversettings;
            ErrorMessage = "";
        }

        private FingerprintDeviceHost Host => FingerprintDeviceHost.GetOrCreate(_serverSetting);

        private bool Connect(AxCZKEM d)
        {
            if (d == null)
                return false;

            // Newer firmware (e.g. eSSL x2008, Ver 6.60 2025) checks the comm key as a string and only
            // accepts it through SetCommPasswordEx (SDK 6.3.1.51+). Older devices use the numeric key 0.
            string commKey = string.IsNullOrEmpty(_serverSetting.FingerPrintMachineCommKey) ? "0" : _serverSetting.FingerPrintMachineCommKey;
            d.SetCommPasswordEx(commKey);

            if (d.Connect_Net(_serverSetting.FingerPrintMachineIp, 4370))
                return true;
            int errorCode = 0;
            d.GetLastError(ref errorCode);
            _logProgress("Error in Attendance: Cannot connect to device (error " + errorCode + ")", true);
            return false;
        }

        private void _logProgress(string msg, bool isError = false, bool isComplete = false)
        {
            if (isError)
                ErrorMessage = msg;
            ReportSyncProgress?.Invoke(null, new AttendanceSyncEventArgs(msg, isComplete));
            Logger.Log(msg);
        }

        // ---- PasUsers writes -----------------------------------------------------------------
        // TEST: Passoft writes these to the PasUsers table.

        private void InsertUser(string userId, string name, bool isActive, double workingHrs, int fingerprintSlots)
        {
            Logger.Log(string.Format("TEST: InsertUser {0} '{1}' active={2} slots={3}", userId, name, isActive, fingerprintSlots));
        }

        internal void UpdateUser(string userId, string name = null, bool? isActive = null,
                                 double? workingHrs = null, int? fingerprintSlots = null)
        {
            Logger.Log(string.Format("TEST: UpdateUser {0} name={1} active={2} slots={3}", userId, name, isActive, fingerprintSlots));
        }

        // ---- device operations -------------------------------------------------------------

        public async Task StartSync(bool forceStart = false, bool syncFingerprints = false)
        {
            try
            {
                var host = Host;
                if (host == null)
                    return;

                string result = "1";   // TEST: Passoft reads this from sp_pb_Attendance
                if (result != "1" && !forceStart)
                    return;

                await host.InvokeAsync((d, ct) =>
                {
                    if (_syncUsers(d, syncFingerprints))
                        _syncAttnLog(d);
                    _logProgress("Completed Sync", false, true);
                    return Task.FromResult<object>(null);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Log("Error in attendance sync", ex);
            }
        }

        private bool _syncUsers(AxCZKEM d, bool syncFingerprints)
        {
            try
            {
                _logProgress("Started syncing users");
                ObservableCollection<AttenUser> users = GetAttenUsers(_serverSetting, true);
                List<int> machineUserList = new List<int>();

                if (!syncFingerprints && users.Count > 0 && users.All(u => u.FingerprintSlots == 0))
                {
                    _logProgress("Downloading fingerprint enrollment status...");
                    syncFingerprints = true;
                }
                string dwEnrollNumber = "";
                string name = "", password = "";
                int privilege = 0;
                bool enabled = false;

                if (!Connect(d))
                    return false;
                if (!d.ReadAllUserID(1))
                {
                    _logProgress("Error in Attendance: Cannot read all users", true);
                    return false;
                }
                int dwMachineNumber = 1;

                while (d.SSR_GetAllUserInfo(dwMachineNumber, out dwEnrollNumber, out name, out password, out privilege, out enabled))
                {
                    AttenUser row = users.Where(w => w.UserId == Converter.ToInt(dwEnrollNumber)).FirstOrDefault();

                    bool needMask = syncFingerprints || row == null;
                    int slotMask = needMask ? _readFingerprintSlotMask(d, dwMachineNumber, dwEnrollNumber) : 0;

                    if (row == null)
                        InsertUser(dwEnrollNumber, name, isActive: true, workingHrs: 9.5, fingerprintSlots: slotMask);
                    else if (needMask && (row.Name != name || row.FingerprintSlots != slotMask))
                        UpdateUser(dwEnrollNumber, name: name, fingerprintSlots: slotMask);
                    else if (row.Name != name)
                        UpdateUser(dwEnrollNumber, name: name);

                    machineUserList.Add(Converter.ToInt(dwEnrollNumber));
                }
                if (machineUserList.Count > 0)
                {
                    foreach (int deletedUser in machineUserList.Where(w => !users.Any(w2 => w2.UserId == w)))
                        UpdateUser(deletedUser.ToString(), isActive: false);
                }
                d.EnableDevice(1, true);
            }
            catch (Exception ex)
            {
                _logProgress("Error in attendance user download: " + ex.Message, true);
                return false;
            }
            finally
            {
                try { d.Disconnect(); } catch { }
            }
            return true;
        }

        private void _syncAttnLog(AxCZKEM d)
        {
            string dwEnrollNumber;

            try
            {
                _logProgress("Started syncing attendance log");
                string lpszProductCode;
                if (!Connect(d))
                    return;
                d.GetProductCode(1, out lpszProductCode);

                int dwYear = -1, dwMonth = -1, dwDay = -1;
                int dwHour = -1, dwMinute = -1, dwSecond = -1, dwWorkCode = -1;
                int dwVerifyMode = -1, dwInOutMode = -1;

                int dwValue = -1;

                if (d.GetDeviceStatus(1, 6, ref dwValue))
                {
                    d.DisableDeviceWithTimeOut(1, 5000);
                    if (d.ReadAllGLogData(1))
                    {
                        bool isDataExists = true;

                        while (isDataExists)
                        {
                            string strEventTime;
                            try
                            {
                                isDataExists = d.SSR_GetGeneralLogData(1, out dwEnrollNumber, out dwVerifyMode, out dwInOutMode, out dwYear, out dwMonth, out dwDay, out dwHour, out dwMinute, out dwSecond, ref dwWorkCode);
                                strEventTime = Convert.ToString(dwYear) + "-" + Convert.ToString(dwMonth) + "-" + Convert.ToString(dwDay) + " " + Convert.ToString(dwHour) + ":" + Convert.ToString(dwMinute) + ":" + Convert.ToString(dwSecond);
                            }
                            catch (Exception)
                            {
                                isDataExists = true;
                                continue;
                            }

                            if (!DBNull.Value.Equals(dwEnrollNumber) && isDataExists
                                    && !dwEnrollNumber.Equals("") && !dwEnrollNumber.Equals("0"))
                            {
                                if (!_addLog(dwEnrollNumber, strEventTime))
                                    throw new Exception("Error while adding to log table");
                            }
                        }
                        //after that you clear the machine log
                        if (d.ClearGLog(1))
                        {
                            d.RefreshData(1);//the data in the device should be refreshed
                        }
                        else
                        {
                            int idwErrorCode = 0;
                            d.GetLastError(ref idwErrorCode);
                            Logger.Log("Clearing log in attendance finger print scanner failed. ErrorCode = " + idwErrorCode.ToString());
                        }
                    }
                }
                // TEST: Passoft updates LAST_ATTEND_SYNC_TIME in PasSettings here.
            }
            catch (Exception ex)
            {
                _logProgress("Error while downloading attendance log. More details " + ex.Message, true);
            }
            finally
            {
                try { d.EnableDevice(1, true); } catch { }
                try { d.Disconnect(); } catch { }
            }
        }

        private bool _addLog(string userId, string eventTime)
        {
            Logger.Log("TEST: PasAttenLog " + userId + " @ " + eventTime);   // TEST: Passoft inserts into PasAttenLog
            return true;
        }

        public static ObservableCollection<AttenUser> GetAttenUsers(ServerSettings serverSetting, bool showInActive = false)
        {
            return new ObservableCollection<AttenUser>();   // TEST: Passoft reads PasUsers
        }

        // Create / rename a user on the device. enrolled=false disables the account.
        public Task<bool> SaveUserToDevice(string userId, string name, string password, int privilege, bool enabled)
        {
            var host = Host;
            if (host == null)
                return Task.FromResult(false);

            return host.InvokeAsync(d =>
            {
                if (!Connect(d)) return false;
                d.EnableDevice(1, false);
                bool success = d.SSR_SetUserInfo(1, userId, name, password, privilege, enabled);
                d.EnableDevice(1, true);
                d.Disconnect();
                return success;
            });
        }

        // Trigger remote fingerprint enrollment on the physical device and wait for the device's
        // real-time OnEnrollFinger event. Runs entirely on the host thread, which owns the
        // control and pumps messages, so the event is delivered without any cross-apartment
        // marshalling. Cancellation flows through the token.
        public Task<bool> PromptUserFingerprintEnrollment(string userId, int fingerIndex, CancellationToken cancellationToken)
        {
            var host = Host;
            if (host == null)
                return Task.FromResult(false);

            return host.InvokeAsync(async (d, ct) =>
            {
                if (!Connect(d)) return false;

                var enrollTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Axzkemkeeper._IZKEMEvents_OnEnrollFingerEventHandler handler =
                    (sender, e) => enrollTcs.TrySetResult(e.actionResult);

                d.OnEnrollFinger += handler;
                try
                {
                    d.RegEvent(1, 65535);   // 65535 = listen to all real-time event groups

                    // StartEnrollEx: (userId, fingerIndex 0-9, flag 1 = fingerprint enrollment)
                    if (!d.StartEnrollEx(userId, fingerIndex, 1))
                        return false;

                    using (ct.Register(() => enrollTcs.TrySetCanceled()))
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                    using (timeout.Token.Register(() => enrollTcs.TrySetResult(-1)))   // -1 = timed out
                    {
                        int result;
                        try
                        {
                            result = await enrollTcs.Task.ConfigureAwait(true);
                        }
                        catch (OperationCanceledException)
                        {
                            try { d.CancelOperation(); } catch { }
                            throw;
                        }

                        if (result == -1)
                        {
                            try { d.CancelOperation(); } catch { }
                            ErrorMessage = "Enrollment timed out.";
                            return false;
                        }
                        if (result != 0)
                        {
                            ErrorMessage = "Enrollment failed (code " + result + ").";
                            return false;
                        }

                        // Confirm the template actually landed on the device.
                        bool landed = (_readFingerprintSlotMask(d, 1, userId) & (1 << fingerIndex)) != 0;
                        if (landed) _updateCachedFingerprintSlot(userId, fingerIndex, true);
                        return landed;
                    }
                }
                finally
                {
                    try { d.OnEnrollFinger -= handler; } catch { }
                    try { d.Disconnect(); } catch { }
                }
            }, cancellationToken);
        }

        // NEW: face enrollment on face terminals. Deletes any existing face (the device refuses to
        // re-enroll over it), starts enrollment on the device, then waits for OnEnrollFinger or
        // the timeout. Face terminals don't always raise the event, so the result is decided by
        // whether a face template exists afterwards.
        public Task<bool> PromptUserFaceEnrollment(string userId, CancellationToken cancellationToken)
        {
            var host = Host;
            if (host == null)
                return Task.FromResult(false);

            return host.InvokeAsync(async (d, ct) =>
            {
                if (!Connect(d)) return false;

                var enrollTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Axzkemkeeper._IZKEMEvents_OnEnrollFingerEventHandler handler =
                    (sender, e) => enrollTcs.TrySetResult(e.actionResult);

                d.OnEnrollFinger += handler;
                try
                {
                    if (_hasFace(d, userId))
                    {
                        if (!d.DelUserFace(1, userId, FaceIndex))
                        {
                            int errorCode = 0;
                            d.GetLastError(ref errorCode);
                            ErrorMessage = "Could not remove the existing face (error " + errorCode + ").";
                            return false;
                        }
                        d.RefreshData(1);
                    }

                    d.RegEvent(1, 65535);
                    d.CancelOperation();
                    d.EnableDevice(1, true);

                    // StartEnrollEx: (userId, 111 = face slot, flag 0)
                    if (!d.StartEnrollEx(userId, FaceIndex, 0))
                    {
                        int errorCode = 0;
                        d.GetLastError(ref errorCode);
                        ErrorMessage = "Could not start face enrollment (error " + errorCode + ").";
                        return false;
                    }

                    using (ct.Register(() => enrollTcs.TrySetCanceled()))
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                    using (timeout.Token.Register(() => enrollTcs.TrySetResult(-1)))
                    {
                        try
                        {
                            await enrollTcs.Task.ConfigureAwait(true);
                        }
                        catch (OperationCanceledException)
                        {
                            try { d.CancelOperation(); } catch { }
                            throw;
                        }
                    }

                    bool landed = _hasFace(d, userId);
                    if (!landed) ErrorMessage = "Face enrollment did not complete.";
                    return landed;
                }
                finally
                {
                    try { d.OnEnrollFinger -= handler; } catch { }
                    try { d.Disconnect(); } catch { }
                }
            }, cancellationToken);
        }

        private bool _hasFace(AxCZKEM d, string userId)
        {
            string template = "";
            int length = 0;
            try
            {
                return d.GetUserFaceStr(1, userId, FaceIndex, ref template, ref length) && !string.IsNullOrEmpty(template);
            }
            catch
            {
                return false;
            }
        }

        // NEW: firmware / serial / platform / record counts, for diagnosing old vs new devices.
        public Task<string> GetDeviceInfo()
        {
            var host = Host;
            if (host == null)
                return Task.FromResult("Attendance device is not available.");

            return host.InvokeAsync(d =>
            {
                if (!Connect(d)) return ErrorMessage;
                try
                {
                    string sdkVersion = "", firmware = "", serial = "", platform = "", productCode = "";
                    int users = 0, fingerprints = 0, logs = 0, faces = 0;
                    d.GetSDKVersion(ref sdkVersion);
                    d.GetFirmwareVersion(1, ref firmware);
                    d.GetSerialNumber(1, out serial);
                    d.GetPlatform(1, ref platform);
                    d.GetProductCode(1, out productCode);
                    d.GetDeviceStatus(1, 2, ref users);
                    d.GetDeviceStatus(1, 3, ref fingerprints);
                    d.GetDeviceStatus(1, 6, ref logs);
                    d.GetDeviceStatus(1, 21, ref faces);
                    return string.Format("SDK {0}, firmware {1}, serial {2}, platform {3}, model {4}; {5} users, {6} fingerprints, {7} faces, {8} logs",
                        sdkVersion, firmware, serial, platform, productCode, users, fingerprints, faces, logs);
                }
                finally
                {
                    try { d.Disconnect(); } catch { }
                }
            });
        }

        // NEW: every user on the device with card, fingerprint slots and face. Users are read first
        // and the per-user template probes done afterwards, so the probes can't disturb the
        // SSR_GetAllUserInfo enumeration. Slow on large devices (11 round-trips per user).
        public Task<List<DeviceUser>> GetDeviceUsers()
        {
            var host = Host;
            if (host == null)
                return Task.FromResult(new List<DeviceUser>());

            return host.InvokeAsync(d =>
            {
                var users = new List<DeviceUser>();
                if (!Connect(d)) return users;
                try
                {
                    d.EnableDevice(1, false);
                    if (!d.ReadAllUserID(1))
                    {
                        _logProgress("Error in Attendance: Cannot read all users", true);
                        return users;
                    }

                    string enrollNumber, name, password;
                    int privilege;
                    bool enabled;
                    while (d.SSR_GetAllUserInfo(1, out enrollNumber, out name, out password, out privilege, out enabled))
                    {
                        string cardNumber;
                        d.GetStrCardNumber(out cardNumber);
                        users.Add(new DeviceUser
                        {
                            UserId = enrollNumber,
                            Name = name,
                            Privilege = privilege,
                            Enabled = enabled,
                            HasPassword = !string.IsNullOrEmpty(password),
                            CardNumber = cardNumber == "0" ? "" : cardNumber
                        });
                    }

                    foreach (var user in users)
                    {
                        user.FingerprintSlots = _readFingerprintSlotMask(d, 1, user.UserId);
                        user.HasFace = _hasFace(d, user.UserId);
                    }
                    return users;
                }
                finally
                {
                    try { d.EnableDevice(1, true); } catch { }
                    try { d.Disconnect(); } catch { }
                }
            });
        }

        // Re-reads one user's fingerprint slot mask from the device (10 slot probes) and writes
        // it to PasUsers.FingerprintSlots, then returns the mask (-1 on failure).
        public async Task<int> RefreshUserFingerprintSlots(string userId)
        {
            ErrorMessage = "";
            var host = Host;
            if (host == null) { ErrorMessage = "Attendance device is not available."; return -1; }

            try
            {
                return await host.InvokeAsync(d =>
                {
                    if (!Connect(d)) return -1;
                    try
                    {
                        int mask = _readFingerprintSlotMask(d, 1, userId);
                        UpdateUser(userId, fingerprintSlots: mask);
                        return mask;
                    }
                    finally
                    {
                        try { d.Disconnect(); } catch { }
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Logger.Log("Error in RefreshUserFingerprintSlots", ex);
                return -1;
            }
        }

        // Reads which of the 10 finger slots hold a template as a bitmask (bit N = slot N).
        // Queries each slot directly (GetUserTmpExStr) instead of a bulk ReadAllTemplate dump,
        // which has been unreliable on this device/SDK; a dropped interop call on one slot is
        // swallowed and treated as unregistered rather than failing the whole read.
        private int _readFingerprintSlotMask(AxCZKEM d, int machineNumber, string enrollNumber)
        {
            int mask = 0;
            for (int idwFingerIndex = 0; idwFingerIndex < 10; idwFingerIndex++)
            {
                try
                {
                    if (d.GetUserTmpExStr(machineNumber, enrollNumber, idwFingerIndex, out int flag, out string sTemplate, out int tmpLength)
                        && !string.IsNullOrEmpty(sTemplate))
                    {
                        mask |= 1 << idwFingerIndex;
                    }
                }
                catch
                {
                    // Suppress interop drop-offs for individual slots.
                }
            }
            return mask;
        }

        private void _updateCachedFingerprintSlot(string userId, int slotIndex, bool registered)
        {
            // TEST: Passoft updates PasUsers.FingerprintSlots here.
            Logger.Log(string.Format("TEST: FingerprintSlots {0} slot {1} = {2}", userId, slotIndex, registered));
        }

        // Delete only a specific finger slot (e.g. slot 0 or slot 1).
        public async Task<bool> DeleteSpecificFingerprint(string userId, int fingerIndex)
        {
            if (fingerIndex < 0 || fingerIndex > 9) return false;

            var host = Host;
            if (host == null) return false;

            bool success = await host.InvokeAsync(d =>
            {
                if (!Connect(d)) return false;
                d.EnableDevice(1, false);
                // Passing the exact finger index (0-9) as the backup number deletes only that slot.
                bool ok = d.SSR_DeleteEnrollData(1, userId, fingerIndex);
                d.EnableDevice(1, true);
                d.Disconnect();
                return ok;
            }).ConfigureAwait(false);

            if (success) _updateCachedFingerprintSlot(userId, fingerIndex, false);
            return success;
        }
    }

    // NEW: one user as stored on the device (see AttendanceSyncManager.GetDeviceUsers).
    public class DeviceUser
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public int Privilege { get; set; }
        public bool Enabled { get; set; }
        public bool HasPassword { get; set; }
        public string CardNumber { get; set; }
        public int FingerprintSlots { get; set; }
        public bool HasFace { get; set; }

        public string Role
        {
            get
            {
                switch (Privilege)
                {
                    case 0: return "User";
                    case 1: return "Enroller";
                    case 2: return "Admin";
                    case 3: return "Super admin";
                    default: return Privilege.ToString();
                }
            }
        }

        // Number of enrolled fingers, from the bitmask.
        public int FingerCount
        {
            get { return Enumerable.Range(0, 10).Count(i => (FingerprintSlots & (1 << i)) != 0); }
        }

        // Enrolled finger slot indexes (0-9) as "0, 3", from the bitmask.
        public string FingerSlots
        {
            get { return string.Join(", ", Enumerable.Range(0, 10).Where(i => (FingerprintSlots & (1 << i)) != 0)); }
        }
    }

    public class AttendanceSyncEventArgs : EventArgs
    {
        private readonly string _statusMessage;
        private readonly bool _isCompleted;

        public AttendanceSyncEventArgs(string statusMessage, bool isCompleted = false)
        {
            _statusMessage = statusMessage;
            _isCompleted = isCompleted;
        }

        public string StatusMessage
        {
            get { return _statusMessage; }
        }

        public bool IsCompleted
        {
            get { return _isCompleted; }
        }
    }
}
