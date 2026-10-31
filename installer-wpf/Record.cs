using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace LeGoMyCursor.Setup
{
    // The Apps & Features record also stores the driver package and certificate that setup owns.
    internal sealed class Record
    {
        private const int ThumbprintLength = 20; // SHA-1 certificate hash

        internal bool Present;
        internal string Oem = "";
        internal byte[] Thumbprint = new byte[ThumbprintLength];

        internal static bool ValidOemName(string path)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string prefix = windows + @"\INF\oem";
            return windows.Length > 0 && path.Length > prefix.Length + 4 &&
                path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) &&
                path.Substring(prefix.Length, path.Length - prefix.Length - 4).All(c => c >= '0' && c <= '9');
        }

        // A missing record returns Present == false; unreadable or invalid records throw.
        internal static Record Read()
        {
            object oem, thumbprint;
            try
            {
                using (var hklm = Platform.Hklm())
                using (var key = hklm.OpenSubKey(Platform.RecordKey))
                {
                    if (key == null)
                        return new Record();
                    oem = key.GetValue("Oem");
                    thumbprint = key.GetValue("Thumbprint");
                }
            }
            catch (Exception e) { throw new Win32Exception(Platform.Code(e)); }
            if (oem == null || thumbprint == null)
                throw new Win32Exception(Platform.ERROR_FILE_NOT_FOUND);
            var record = new Record { Present = true, Oem = oem as string, Thumbprint = thumbprint as byte[] };
            if (record.Oem == null || record.Thumbprint == null)
                throw new Win32Exception(Platform.ERROR_UNSUPPORTED_TYPE);
            if (record.Thumbprint.Length != ThumbprintLength || !ValidOemName(record.Oem))
                throw new Win32Exception(Platform.ERROR_INVALID_DATA);
            return record;
        }

        internal static void Write(string oem, byte[] thumbprint)
        {
            if (thumbprint == null || thumbprint.Length != ThumbprintLength)
                throw new Win32Exception(Platform.ERROR_INVALID_PARAMETER);
            int status = 0;
            try
            {
                string directory = Platform.InstallDirectory();
                string executable = Path.Combine(directory, "LeGoMyCursor.Setup.exe");
                using (var hklm = Platform.Hklm())
                using (var key = hklm.CreateSubKey(Platform.RecordKey))
                {
                    key.SetValue("DisplayName", "LeGo My Cursor");
                    key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
                    key.SetValue("Publisher", "LeGo My Cursor contributors");
                    key.SetValue("UninstallString", "\"" + executable + "\" /uninstall");
                    key.SetValue("ModifyPath", "\"" + executable + "\"");
                    key.SetValue("InstallLocation", directory);
                    key.SetValue("DisplayIcon", executable);
                    key.SetValue("NoModify", 0, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("Oem", oem);
                    key.SetValue("Thumbprint", thumbprint, RegistryValueKind.Binary);
                }
            }
            catch (Exception e) { status = Platform.Code(e); }
            Platform.Log("Write Apps & Features record: " + status);
            if (status != 0)
                throw new Win32Exception(status);
        }

        internal static void Delete()
        {
            Platform.Stage("Finishing cleanup…");
            int status = Platform.DeleteTree(Platform.RecordKey);
            Platform.Log("Delete Apps & Features record: " + status);
            if (status != 0 && status != Platform.ERROR_FILE_NOT_FOUND)
                throw new Win32Exception(status);
        }
    }
}
