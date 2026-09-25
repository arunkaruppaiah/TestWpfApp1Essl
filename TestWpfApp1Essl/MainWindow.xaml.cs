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

        private async Task Run(string name, Func<AttendanceSyncManager, Task<string>> op)
        {
            IsEnabled = false;
            var manager = NewManager();
            try
            {
                Logger.Log(name + "...");
                string result = await op(manager);
                Logger.Log(name + ": " + result + (string.IsNullOrEmpty(manager.ErrorMessage) ? "" : " - " + manager.ErrorMessage));
            }
            catch (Exception ex)
            {
                Logger.Log(name + " failed", ex);
            }
            finally
            {
                IsEnabled = true;
            }
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
            Logger.Log("Place finger on the device (90s timeout)");
            await Run("Enroll finger", async m => (await m.PromptUserFingerprintEnrollment(userId, finger, CancellationToken.None)).ToString());
        }

        private async void EnrollFace_Click(object sender, RoutedEventArgs e)
        {
            string userId = UserId;
            Logger.Log("Look at the device (90s timeout)");
            await Run("Enroll face", async m => (await m.PromptUserFaceEnrollment(userId, CancellationToken.None)).ToString());
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
