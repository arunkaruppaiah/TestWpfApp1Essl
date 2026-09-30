using Passoft.Manager;
using Pasumai.Common;
using Pasumai.Common.Model;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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

        private async Task Run(string name, Func<AttendanceSyncManager, Task<string>> op, Button keepEnabled = null)
        {
            _setBusy(true, keepEnabled);
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
                _setBusy(false, null);
            }
        }

        // Disables the inputs and every action button except keepEnabled (the running enrollment,
        // which doubles as its Cancel button).
        private void _setBusy(bool busy, Button keepEnabled)
        {
            panelInputs.IsEnabled = !busy;
            foreach (var child in panelActions.Children)
                if (child is Button button)
                    button.IsEnabled = !busy || button == keepEnabled;
        }

        // Set while an enrollment is waiting on the device; clicking its button again cancels it.
        private CancellationTokenSource _enrollCts;

        // Runs an enrollment with its button turned into "Cancel (Ns)" counting down to the timeout.
        private async Task RunEnrollment(Button button, string name, Func<AttendanceSyncManager, CancellationToken, Task<bool>> op)
        {
            object label = button.Content;
            int remaining = AttendanceSyncManager.EnrollTimeoutSeconds;
            var countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            countdown.Tick += (s, e) =>
            {
                if (remaining > 0) remaining--;
                button.Content = "Cancel (" + remaining + "s)";
            };

            using (var cts = new CancellationTokenSource())
            {
                _enrollCts = cts;
                button.Content = "Cancel (" + remaining + "s)";
                countdown.Start();
                try
                {
                    await Run(name, async m => (await op(m, cts.Token)).ToString(), button);
                }
                finally
                {
                    countdown.Stop();
                    button.Content = label;
                    _enrollCts = null;
                }
            }
        }

        // True if an enrollment is running, in which case its button click cancels it.
        private bool _cancelEnrollment()
        {
            if (_enrollCts == null) return false;
            if (!_enrollCts.IsCancellationRequested)
            {
                Logger.Log("Cancelling enrollment...");
                _enrollCts.Cancel();
            }
            return true;
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
            if (_cancelEnrollment()) return;

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

            Logger.Log("Place finger on the device");
            await RunEnrollment(btnEnrollFinger, "Enroll finger", (m, ct) => m.PromptUserFingerprintEnrollment(userId, finger, exists, ct));
        }

        private async void EnrollFace_Click(object sender, RoutedEventArgs e)
        {
            if (_cancelEnrollment()) return;

            string userId = UserId;

            bool exists = false;
            await Run("Check face", async m => (exists = await m.HasFace(userId)) ? "already enrolled" : "empty");
            if (exists && MessageBox.Show(this,
                    "User " + userId + " already has a face enrolled." + Environment.NewLine + Environment.NewLine + "Delete it and register a new one?",
                    "Replace face", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                Logger.Log("Enroll face: skipped, existing face kept");
                return;
            }

            Logger.Log("Look at the device");
            await RunEnrollment(btnEnrollFace, "Enroll face", (m, ct) => m.PromptUserFaceEnrollment(userId, exists, ct));
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
