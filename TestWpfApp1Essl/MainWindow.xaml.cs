using Axzkemkeeper;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace TestWpfApp1Essl
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        Axzkemkeeper.AxCZKEM axCZKEM1;
        public string ErrorMessage { get; set; }

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (Connect())
            {
                System.Windows.MessageBox.Show("Connected to the device successfully.");
            }
        }

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            AddUser(axCZKEM1, 1, "1001", "Pravin_Test", "123", 0, true);
            /*var methods = axCZKEM1.GetType()
                        .GetMethods(
                            BindingFlags.Public |
                            BindingFlags.Instance |
                            BindingFlags.FlattenHierarchy)
                        .Where(m =>
                            m.Name.IndexOf("Face", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.Name.IndexOf("Enroll", StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(m => m.Name);

            foreach (var method in methods)
            {
                System.Diagnostics.Debug.WriteLine(method.ToString());
            }*/
        }

        private void EnrollFace_Click(object sender, RoutedEventArgs e)
        {
            EnrollFingerFace(1, "1001", 111);
        }

        public bool CopyDll(string dllFileName)
        {
            string currentDir = Directory.GetCurrentDirectory();
            string sourceDir = currentDir + @"\Ref";
            string fileName = @"\" + dllFileName;
            if (!(File.Exists(currentDir + fileName)))
            {
                //Copy the required file
                if (File.Exists(sourceDir + fileName))
                    File.Copy(sourceDir + fileName, currentDir + fileName);
                return true;
            }
            return false;
        }

        public bool Connect()
        {
            CopyDll("tcpcomm.dll");

            // Instantiate the ActiveX wrapper (matching eTimeTrackLite)
            axCZKEM1 = new Axzkemkeeper.AxCZKEM();

            // Required to initialize ActiveX control handle
            axCZKEM1.CreateControl();

            string ip = "192.168.29.201";

            // Use the extended string method identified from eTimeTrackLite
            axCZKEM1.SetCommPasswordEx("000000");

            bool bConnected = axCZKEM1.Connect_Net(ip, 4370);

            int result = 0;

            axCZKEM1.GetLastError(ref result);

            if (bConnected)
            {
                return true;
            }
            else
            {
                System.Windows.MessageBox.Show("Failed to connect to the device - " + result.ToString());
                return false;
            }
        }



        private void DownloadData_Click(object sender, RoutedEventArgs e)
        {
            _syncAttnLog(axCZKEM1);
        }

        public bool AddUser(
                Axzkemkeeper.AxCZKEM zk,
                int machineNumber,
                string enrollNumber,
                string name,
                string password = "",
                int privilege = 0,
                bool enabled = true)
        {
            if (zk == null)
            {
                if (!Connect())
                {
                    return false;
                }
            }

            try
            {
                // 1. Lock terminal UI to prevent concurrent punch/sync interrupts
                zk.EnableDevice(machineNumber, false);

                // 4. Write user details to terminal memory cache
                bool success = zk.SSR_SetUserInfo(
                    machineNumber,
                    enrollNumber,
                    name,
                    password,
                    privilege,
                    enabled
                );

                if (!success)
                {
                    int errorCode = 0;
                    zk.GetLastError(ref errorCode);
                    System.Windows.MessageBox.Show($"[Error] SSR_SetUserInfo failed for user '{enrollNumber}'. Error Code: {errorCode}");
                    return false;
                }
                else
                {
                    System.Windows.MessageBox.Show("Success");
                }

                // 3. Commit cached record into terminal flash ROM (mandatory for persistence)
                zk.RefreshData(machineNumber);
                return true;
            }
            finally
            {
                // 4. Always restore device display and keypad
                zk.EnableDevice(machineNumber, true);
            }
        }


        public bool EnrollFingerFace(int machineNumber, string userId, int fingerIndex)
        {
            int flag = 0;

            DeleteFingerFace(machineNumber, userId, fingerIndex);

            // Make sure any previous operation is cancelled
            axCZKEM1.CancelOperation();

            // Make sure device is enabled before starting enrollment
            axCZKEM1.EnableDevice(machineNumber, true);

            bool result = axCZKEM1.StartEnrollEx(
                userId,
                fingerIndex,
                flag
            );

            return result;
        }

        private bool DeleteFingerFace(int machineNumber, string userId, int fingerIndex)
        {
            string existingTmp = "";
            int tmpLength = 0;

            // SSR_GetUserTmpStr checks if the finger index contains biometric data
            bool exists = false;

            if (fingerIndex == 111)
                exists = axCZKEM1.GetUserFaceStr(machineNumber, userId, fingerIndex, ref existingTmp, ref tmpLength);
            else
                exists = axCZKEM1.SSR_GetUserTmpStr(machineNumber, userId, fingerIndex, out existingTmp, out tmpLength);

            if (exists && !string.IsNullOrEmpty(existingTmp))
            {
                bool isDeleted;
                if (fingerIndex == 111)
                    isDeleted = axCZKEM1.DelUserFace(machineNumber, userId, fingerIndex);
                else
                    isDeleted = axCZKEM1.SSR_DelUserTmp(machineNumber, userId, fingerIndex);
                if (!isDeleted)
                {
                    int errorCode = 0;
                    axCZKEM1.GetLastError(ref errorCode);
                    Console.WriteLine($"Failed to delete existing fingerprint. Error code: {errorCode}");
                    return false;
                }
            }
            // Crucial - force terminal to reload its memory cache
            axCZKEM1.RefreshData(1);
            return true;
        }

        private void _syncAttnLog(AxCZKEM d)
        {
            string dwEnrollNumber;

            try
            {
                string lpszProductCode;
                if (!Connect())
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
                        }
                    }
                }
            }
            catch (Exception ex)
            {

            }
            finally
            {
                try { d.EnableDevice(1, true); } catch { }
                try { d.Disconnect(); } catch { }
            }
        }


    }
}