using Passoft.Manager;
using Pasumai.Common;
using Pasumai.Common.Model;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace TestWpfApp1Essl
{
    // Test harness only. Device code lives in Manager\ and mirrors Passoft.
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Logger.Logged += msg => Dispatcher.BeginInvoke(new Action(() => txtLog.AppendText(msg + Environment.NewLine)));
        }

        private AttendanceSyncManager NewManager()
        {
            var settings = new ServerSettings
            {
                FingerPrintMachineIp = txtIp.Text.Trim(),
                FingerPrintMachineCommKey = txtCommKey.Text.Trim()
            };
            return new AttendanceSyncManager(settings);
        }

        private string UserId => txtUserId.Text.Trim();

        private int FingerIndex => int.TryParse(txtFinger.Text, out int i) ? i : 0;

        // Set while an enrollment is waiting on the device; Cancel enrollment cancels it.
        private CancellationTokenSource _enrollCts;

        private async Task Run(string name, Func<AttendanceSyncManager, Task<string>> op)
        {
            panelInputs.IsEnabled = panelActions.IsEnabled = false;
            var manager = NewManager();
            try
            {
                Logger.Log(name + "...");
                string result = await op(manager);
                Logger.Log(name + ": " + result + (string.IsNullOrEmpty(manager.ErrorMessage) ? "" : " - " + manager.ErrorMessage));
            }
            catch (OperationCanceledException)
            {
                Logger.Log(name + ": cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log(name + " failed", ex);
            }
            finally
            {
                panelInputs.IsEnabled = panelActions.IsEnabled = true;
            }
        }

        // Runs an enrollment with the Cancel button live for its duration.
        private Task RunEnrollment(string name, Func<AttendanceSyncManager, CancellationToken, Task<bool>> op)
        {
            return RunCancellable(name, async (m, ct) => (await op(m, ct)).ToString());
        }

        private async Task RunCancellable(string name, Func<AttendanceSyncManager, CancellationToken, Task<string>> op)
        {
            using (var cts = new CancellationTokenSource())
            {
                _enrollCts = cts;
                btnCancel.IsEnabled = true;
                try
                {
                    await Run(name, m => op(m, cts.Token));
                }
                finally
                {
                    btnCancel.IsEnabled = false;
                    _enrollCts = null;
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_enrollCts == null) return;
            Logger.Log("Cancelling...");
            btnCancel.IsEnabled = btnDone.IsEnabled = false;
            _enrollCts.Cancel();
        }

        // Set during face enrollment: the device raises no event when a face is captured, so the
        // user clicks Done once the device shows success.
        private CancellationTokenSource _finishCts;

        private void Done_Click(object sender, RoutedEventArgs e)
        {
            if (_finishCts == null) return;
            Logger.Log("Done clicked - checking the device for the face...");
            btnCancel.IsEnabled = btnDone.IsEnabled = false;
            _finishCts.Cancel();
        }

        private async void DeviceInfo_Click(object sender, RoutedEventArgs e)
        {
            await Run("Device info", async m =>
            {
                string info = await m.GetDeviceInfo();
                txtDeviceInfo.Text = info;
                List<DeviceUser> users = await m.GetDeviceUsers();
                gridUsers.ItemsSource = users;
                return info + Environment.NewLine + users.Count + " users listed";
            });
        }

        // Picking a row fills User ID / Name so the enroll/delete buttons act on that user.
        private void GridUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var user = gridUsers.SelectedItem as DeviceUser;
            if (user == null) return;
            txtUserId.Text = user.UserId;
            txtName.Text = user.Name;
        }

        private async void AddUser_Click(object sender, RoutedEventArgs e)
        {
            string userId = UserId, name = txtName.Text.Trim();
            await Run("Add user", async m => (await m.SaveUserToDevice(userId, name, null, 0, true)).ToString());
        }

        private async void EnrollFinger_Click(object sender, RoutedEventArgs e)
        {
            string userId = UserId;
            int finger = FingerIndex;

            bool exists = false;
            await Run("Check finger " + finger, async m => (exists = await m.HasFingerprint(userId, finger)) ? "already enrolled" : "empty");
            if (exists && MessageBox.Show(this,
                    "User " + userId + " already has finger " + finger + " enrolled.\n\nDelete it and register a new one?",
                    "Replace fingerprint", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                Logger.Log("Enroll finger: skipped, existing fingerprint kept");
                return;
            }

            Logger.Log("Place finger on the device (90s timeout)");
            await RunEnrollment("Enroll finger", (m, ct) => m.PromptUserFingerprintEnrollment(userId, finger, exists, ct));
        }

        private async void EnrollFace_Click(object sender, RoutedEventArgs e)
        {
            string userId = UserId;
            Logger.Log("Look at the device, then click Done when it shows success (90s timeout)");
            using (var finish = new CancellationTokenSource())
            {
                _finishCts = finish;
                btnDone.IsEnabled = true;
                try
                {
                    await RunEnrollment("Enroll face", (m, ct) => m.PromptUserFaceEnrollment(userId, ct, finish.Token));
                }
                finally
                {
                    btnDone.IsEnabled = false;
                    _finishCts = null;
                }
            }
        }

        private async void ListenEvents_Click(object sender, RoutedEventArgs e)
        {
            Logger.Log("Punch / verify on the device now (60s, Cancel to stop)");
            await RunCancellable("Listen events", (m, ct) => m.ListenForDeviceEvents(TimeSpan.FromSeconds(60), ct));
        }

        private async void DeleteFinger_Click(object sender, RoutedEventArgs e)
        {
            string userId = UserId;
            int finger = FingerIndex;
            await Run("Delete finger", async m => (await m.DeleteSpecificFingerprint(userId, finger)).ToString());
        }

        private async void Sync_Click(object sender, RoutedEventArgs e)
        {
            await Run("Sync", async m => { await m.StartSync(forceStart: true); return "done"; });
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            FingerprintDeviceHost.ShutdownShared();
        }
    }
}
