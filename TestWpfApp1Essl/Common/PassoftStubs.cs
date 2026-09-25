using System;
using System.Diagnostics;
using System.IO;

// Minimal stand-ins for the Pasumai.Common types that Manager\*.cs uses, with the same names and
// namespaces as Passoft so those files compile here unchanged. Do NOT copy this file to Passoft.

namespace Pasumai.Common
{
    public static class Logger
    {
        // Test-only: lets the window show the log.
        public static event Action<string> Logged;

        public static void Log(string message)
        {
            message = DateTime.Now.ToString("HH:mm:ss") + " " + message;
            Debug.WriteLine(message);
            Logged?.Invoke(message);
        }

        public static void Log(string message, Exception ex)
        {
            if (ex != null)
                message += ". More details: " + ex;
            Log(message);
        }
    }

    public static class Utilities
    {
        // Same as Passoft's Utilities.CopyDll.
        public static bool CopyDll(string dllFileName)
        {
            string currentDir = Directory.GetCurrentDirectory();
            string sourceDir = currentDir + @"\Ref";
            string fileName = @"\" + dllFileName;
            if (!(File.Exists(currentDir + fileName)))
            {
                //Copy the required file
                if (File.Exists(sourceDir + @"\" + dllFileName))
                    sourceDir += "";
                else if (IntPtr.Size == 8)
                    sourceDir += @"\64bit";
                else
                    sourceDir += @"\32bit";
                if (File.Exists(sourceDir + fileName))
                    File.Copy(sourceDir + fileName, currentDir + fileName);
                return true;
            }
            return false;
        }

        // Same as Passoft's Utilities.RegisterDll.
        public static bool RegisterDll(string filePath)
        {
            try
            {
                string currentDir = Directory.GetCurrentDirectory();
                Process reg = new Process();
                reg.StartInfo.FileName = "regsvr32.exe";
                reg.StartInfo.Arguments = "/s \"" + currentDir + filePath + "\"";
                reg.StartInfo.Verb = "runas";
                reg.StartInfo.UseShellExecute = true;
                reg.Start();
                reg.WaitForExit();
                reg.Close();
            }
            catch (Exception ex)
            {
                Logger.Log("Error registering dll. More details: " + ex.ToString());
                return false;
            }
            return true;
        }
    }

    public static class Converter
    {
        public static int ToInt(string value)
        {
            int result;
            return int.TryParse(value, out result) ? result : 0;
        }
    }

    public static class ExtensionHelper
    {
        public static bool ContainsIgnoreCase(this string mainString, string subString)
        {
            return mainString != null && subString != null
                && mainString.IndexOf(subString, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public static class Constants
    {
        public enum EnumAttendanceType
        {
            None = 0,
            ManualLogin = 1,
            FingerPrintMachine = 2
        }
    }
}

namespace Pasumai.Common.Model
{
    public class ServerSettings
    {
        public bool IsServerLocal { get; set; } = true;
        public Constants.EnumAttendanceType AttendanceType { get; set; } = Constants.EnumAttendanceType.FingerPrintMachine;
        public string FingerPrintMachineIp { get; set; }

        // NEW - not in Passoft yet. Needs a FINGER_PRINT_MACHINE_COMM_KEY setting loaded the same
        // way as FINGER_PRINT_MACHINE_IP (Settings.cs).
        public string FingerPrintMachineCommKey { get; set; }
    }

    public class AttenUser
    {
        public int UserId { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
        public int FingerprintSlots { get; set; }
    }
}
