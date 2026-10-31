using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace LeGoMyCursor.Setup
{
    internal enum PackageIdentity { LeGoMyCursor, Other, Missing, Error }
    internal enum ActiveState { Active, Inactive, NoDevice }
    internal sealed class RestartResult { internal int Total, Restarted, Error; }

    // Windows operations for setup. Failures throw Win32Exception; "Fail" also logs like lmc::Fail.
    internal static class Platform
    {
        internal const string Subject = "CN=LeGoMyCursor local signer (key destroyed)";
        internal const string RecordKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LeGoMyCursor";
        internal const string ParametersKey =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\legomycursor\Parameters";
        // X.Y.Z, matching the DisplayVersion that Record writes; the assembly's fourth part is always 0.
        internal static readonly Version SetupVersion =
            new Version(Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
        private const string ProtectedSddl = "O:BAD:PAI(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)";
        private static readonly string[] Payload = { "legomycursor.inf", "legomycursor.dll", "legomycursor.cat" };
        private static readonly string[] Stores = { "Root", "TrustedPublisher" };
        private const string Touchpad = @"HID\VID_17EF&UP:000D_U:0005";
        private static readonly DevPropKey HardwareIds =
            new DevPropKey { Category = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 3 };
        private static readonly DevPropKey DriverInfPath =
            new DevPropKey { Category = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), Id = 5 };
        private static readonly DevPropKey DeviceStack =
            new DevPropKey { Category = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), Id = 14 };
        private const int VerifyAttempts = 11;
        private const int VerifyRetryMilliseconds = 500;
        [ThreadStatic] internal static Action<string> StageSink;

        // Driver Parameters\Mode values; Simple is the driver default.
        internal const int ModeOff = 0;
        internal const int ModeSimple = 1;
        internal const int ModeCompliant = 2;

        internal const int ERROR_FILE_NOT_FOUND = 2;
        internal const int ERROR_PATH_NOT_FOUND = 3;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_INVALID_DATA = 13;
        internal const int ERROR_FILE_EXISTS = 80;
        internal const int ERROR_INVALID_PARAMETER = 87;
        internal const int ERROR_INVALID_NAME = 123;
        internal const int ERROR_ALREADY_EXISTS = 183;
        internal const int ERROR_NO_MORE_ITEMS = 259;
        internal const int ERROR_INSTALL_FAILURE = 1603;
        internal const int ERROR_UNSUPPORTED_TYPE = 1630;
        internal const int ERROR_PRODUCT_VERSION = 1638;
        internal const int ERROR_RESOURCE_DATA_NOT_FOUND = 1812;
        internal const int ERROR_SUCCESS_REBOOT_REQUIRED = 3010;
        internal const int ERROR_REPARSE_POINT_ENCOUNTERED = 4319;
        internal const int ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION = 4551;

        internal static Win32Exception Fail(string operation, int error)
        {
            Log(operation + " failed: 0x" + error.ToString("x8"));
            return new Win32Exception(error);
        }

        // Recovers the Win32 code from .NET I/O, registry, and crypto exceptions.
        internal static int Code(Exception error)
        {
            var win32 = error as Win32Exception;
            if (win32 != null)
                return win32.NativeErrorCode;
            if (error is SecurityException)
                return ERROR_ACCESS_DENIED;
            return (error.HResult & 0xFFFF0000) == 0x80070000 ? error.HResult & 0xFFFF : error.HResult;
        }

        internal static RegistryKey Hklm()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }

        internal static int DeleteTree(string key) { return RegDeleteTree(new IntPtr(HKEY_LOCAL_MACHINE), key); }

        private static string Folder(Environment.SpecialFolder folder, string name)
        {
            string path = Environment.GetFolderPath(folder);
            if (path.Length == 0)
                throw new Win32Exception(ERROR_PATH_NOT_FOUND, "Windows could not locate the " + folder + " folder.");
            return Path.Combine(path, name);
        }

        internal static string LogPath()
        {
            return Folder(Environment.SpecialFolder.CommonApplicationData, @"LeGoMyCursor\install.log");
        }

        // FILE_APPEND_DATA alone makes every write an atomic append, so processes can share the file.
        private static FileStream OpenLog()
        {
            return new FileStream(LogPath(), FileMode.Append, FileSystemRights.AppendData, FileShare.ReadWrite,
                4096, FileOptions.None);
        }

        internal static void Log(string message)
        {
            byte[] line = Encoding.UTF8.GetBytes(
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " +
                message + "\r\n");
            try
            {
                using (var file = OpenLog())
                {
                    file.Write(line, 0, line.Length);
                    file.Flush(true);
                }
            }
            catch (Exception error) { Trace.WriteLine(message + " (log unavailable: " + error.Message + ")"); }
            // A GUI executable has no console normally; /probe attaches to its caller.
            try
            {
                using (var output = Console.OpenStandardOutput())
                    output.Write(line, 0, line.Length);
            }
            catch (IOException) { }
        }

        internal static void InitLog(string version, string command)
        {
            int error = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath()));
                using (OpenLog())
                {
                    // Opening the log is the check.
                }
            }
            catch (Exception e) { error = Code(e); }
            Log("=== LeGo My Cursor " + version + "; command=" + command +
                "; log-open=0x" + error.ToString("x8") + " ===");
            try
            {
                using (var hklm = Hklm())
                using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    Log("Windows " + key.GetValue("CurrentBuild", "") + "." + key.GetValue("UBR", 0) +
                        " (" + key.GetValue("DisplayVersion", "") + ")");
                }
            }
            catch (Exception e) { Log("Windows version unavailable: " + e.Message); }
            try
            {
                Devices((set, device) =>
                {
                    string[] ids = Property(set, device, HardwareIds);
                    if (!HasId(ids, "VID_17EF"))
                        return;
                    Log("Device " + InstanceId(set, device) +
                        "; INF=" + Property(set, device, DriverInfPath).FirstOrDefault());
                    foreach (string id in ids)
                        Log("  hardware ID: " + id);
                });
            }
            catch (Win32Exception) { } // Devices logged the failure; startup continues without the device list.
        }

        internal static void OpenLogFolder()
        {
            Process.Start("explorer.exe", "/select,\"" + LogPath() + "\"")?.Dispose();
        }

        internal static void Stage(string text)
        {
            try
            {
                StageSink?.Invoke(text);
            }
            catch { Log("Progress presentation failed; operation continues without retry"); }
            Log("Current operation: " + text);
        }

        internal static bool CreateProtectedDirectory(string path)
        {
            IntPtr descriptor;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(ProtectedSddl, SDDL_REVISION_1, out descriptor,
                IntPtr.Zero))
            {
                return false;
            }
            try
            {
                var security = new SecurityAttributes
                {
                    Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                    Descriptor = descriptor
                };
                return CreateDirectory(path, ref security); // Fails if the path exists, so the DACL always applies.
            }
            finally { LocalFree(descriptor); }
        }

        // Creates <ProgramData>\LeGoMyCursor\work\<new GUID> with an Administrators/SYSTEM-only DACL.
        internal static string CreateWorkDirectory()
        {
            string root = Folder(Environment.SpecialFolder.CommonApplicationData, "LeGoMyCursor");
            string work = Path.Combine(root, "work");
            string leaf = null;
            int error = 0;
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception e) { error = Code(e); }
            if (error == 0 && !CreateProtectedDirectory(work) &&
                (error = Marshal.GetLastWin32Error()) == ERROR_ALREADY_EXISTS)
            {
                // Users can create ProgramData folders: reject existing links; reclaim other work directories.
                try
                {
                    if ((File.GetAttributes(work) & FileAttributes.ReparsePoint) != 0)
                        error = ERROR_REPARSE_POINT_ENCOUNTERED;
                    else
                    {
                        var security = new DirectorySecurity();
                        security.SetSecurityDescriptorSddlForm(ProtectedSddl,
                            AccessControlSections.Owner | AccessControlSections.Access);
                        Directory.SetAccessControl(work, security);
                        error = 0;
                    }
                }
                catch (Exception e) { error = Code(e); }
            }
            if (error == 0)
            {
                // Hide the protected work area from the log folder view.
                try
                {
                    File.SetAttributes(work, File.GetAttributes(work) | FileAttributes.Hidden);
                }
                catch (Exception e) { Log("Hide work directory: 0x" + Code(e).ToString("x8")); }
                // The leaf is a fresh GUID, so ERROR_ALREADY_EXISTS there is a failure.
                leaf = Path.Combine(work, Guid.NewGuid().ToString("B").ToUpperInvariant());
                if (!CreateProtectedDirectory(leaf))
                    error = Marshal.GetLastWin32Error();
            }
            if (error != 0)
                throw Fail("Create work directory", error);
            return leaf;
        }

        internal static string ExtractPayload()
        {
            string directory = CreateWorkDirectory();
            foreach (string name in Payload)
            {
                using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (input == null)
                    {
                        DeletePayload(directory);
                        throw Fail("Load payload resource", ERROR_RESOURCE_DATA_NOT_FOUND);
                    }
                    try
                    {
                        using (var output = new FileStream(Path.Combine(directory, name), FileMode.CreateNew,
                            FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                        }
                    }
                    catch (Exception e)
                    {
                        DeletePayload(directory);
                        throw Fail("Write payload", Code(e));
                    }
                }
            }
            Log("Extracted payload to " + directory);
            return directory;
        }

        internal static void DeletePayload(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;
            foreach (string name in Payload)
            {
                string path = Path.Combine(directory, name);
                try
                {
                    File.Delete(path);
                }
                catch (Exception e) { Log("Delete " + path + ": 0x" + Code(e).ToString("x8")); }
            }
            try
            {
                Directory.Delete(directory);
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception e) { Log("Remove payload directory: 0x" + Code(e).ToString("x8")); }
        }

        // Reads the INF's original name; error holds the Win32 code for Missing/Error.
        internal static PackageIdentity IdentifyPackage(string inf, out int error)
        {
            error = 0;
            if (GetFileAttributes(inf) == INVALID_FILE_ATTRIBUTES)
            {
                error = Marshal.GetLastWin32Error();
                if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
                    return PackageIdentity.Missing;
            }
            int size;
            SetupGetInfInformation(inf, INFINFO_INF_NAME_IS_ABSOLUTE, null, 0, out size);
            var info = new byte[size];
            var original = new OriginalFileInfo { Size = Marshal.SizeOf(typeof(OriginalFileInfo)) };
            if (size == 0 || !SetupGetInfInformation(inf, INFINFO_INF_NAME_IS_ABSOLUTE, info, size, out size) ||
                !SetupQueryInfOriginalFileInformation(info, 0, IntPtr.Zero, ref original))
            {
                error = Marshal.GetLastWin32Error();
                if (error == 0)
                    error = ERROR_INVALID_DATA;
                Log("Inspect " + inf + ": 0x" + error.ToString("x8"));
                return PackageIdentity.Error;
            }
            return string.Equals(original.InfName, "legomycursor.inf", StringComparison.OrdinalIgnoreCase)
                ? PackageIdentity.LeGoMyCursor
                : PackageIdentity.Other;
        }

        // Throws only when the directory scan fails; unreadable INFs are logged and returned in skipped.
        internal static List<string> FindPackages(out List<string> skipped)
        {
            var packages = new List<string>();
            skipped = new List<string>();
            string[] files;
            try
            {
                files = Directory.GetFiles(Folder(Environment.SpecialFolder.Windows, "INF"), "oem*.inf");
            }
            catch (Exception e) { throw Fail("Enumerate OEM packages", Code(e)); }
            foreach (string path in files)
            {
                int error;
                var identity = IdentifyPackage(path, out error);
                if (identity == PackageIdentity.Error)
                {
                    skipped.Add(path);
                    Log("Skipped unreadable package " + path);
                }
                if (identity == PackageIdentity.LeGoMyCursor)
                {
                    packages.Add(path);
                    Log("Found LeGoMyCursor package " + path);
                }
            }
            return packages;
        }

        // Visits present HID devices of all classes; throws after visiting if enumeration fails.
        private static void Devices(Action<IntPtr, DevInfoData> action)
        {
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "HID", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == new IntPtr(INVALID_HANDLE_VALUE))
                throw Fail("SetupDiGetClassDevs", Marshal.GetLastWin32Error());
            int error;
            try
            {
                for (int index = 0; ; ++index)
                {
                    var device = new DevInfoData { Size = Marshal.SizeOf(typeof(DevInfoData)) };
                    if (!SetupDiEnumDeviceInfo(set, index, ref device))
                    {
                        error = Marshal.GetLastWin32Error();
                        break;
                    }
                    action(set, device);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            if (error != ERROR_NO_MORE_ITEMS)
                throw Fail("SetupDiEnumDeviceInfo", error); // ERROR_NO_MORE_ITEMS ends the list.
        }

        // Returns a DEVPROP_TYPE_STRING or STRING_LIST property's strings; empty when absent or another type.
        private static string[] Property(IntPtr set, DevInfoData device, DevPropKey key)
        {
            uint type;
            int size;
            SetupDiGetDeviceProperty(set, ref device, ref key, out type, null, 0, out size, 0);
            var buffer = new byte[size];
            if (size == 0 || !SetupDiGetDeviceProperty(set, ref device, ref key, out type, buffer, size, out size, 0) ||
                (type != DEVPROP_TYPE_STRING && type != DEVPROP_TYPE_STRING_LIST))
            {
                return new string[0];
            }
            return Encoding.Unicode.GetString(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool HasId(string[] ids, string pattern)
        {
            return ids.Any(id => id.ToUpperInvariant().Contains(pattern));
        }

        private static string InstanceId(IntPtr set, DevInfoData device)
        {
            var instance = new StringBuilder(MAX_DEVICE_ID_LEN);
            SetupDiGetDeviceInstanceId(set, ref device, instance, instance.Capacity, IntPtr.Zero);
            return instance.ToString();
        }

        internal static RestartResult RestartTouchpads()
        {
            var result = new RestartResult();
            try
            {
                Devices((set, device) =>
                {
                    if (!HasId(Property(set, device, HardwareIds), Touchpad))
                        return;
                    ++result.Total;
                    var change = new PropChangeParams
                    {
                        HeaderSize = SP_CLASSINSTALL_HEADER_SIZE,
                        InstallFunction = DIF_PROPERTYCHANGE,
                        StateChange = DICS_PROPCHANGE,
                        Scope = DICS_FLAG_GLOBAL
                    };
                    bool restarted =
                        SetupDiSetClassInstallParams(set, ref device, ref change,
                            Marshal.SizeOf(typeof(PropChangeParams))) &&
                        SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref device);
                    int error = restarted ? 0 : Marshal.GetLastWin32Error();
                    Log("Restart " + InstanceId(set, device) + ": 0x" + error.ToString("x8"));
                    if (restarted)
                        ++result.Restarted;
                    else if (result.Error == 0)
                        result.Error = error;
                });
            }
            catch (Win32Exception e)
            {
                if (result.Error == 0)
                    result.Error = e.NativeErrorCode;
            }
            Log("Touchpad restarts: " + result.Restarted + " of " + result.Total + "; Win32=" + result.Error);
            return result;
        }

        private static string ShortcutPath()
        {
            return Folder(Environment.SpecialFolder.CommonPrograms, "LeGo My Cursor.lnk");
        }

        internal static void CreateShortcut(string executable)
        {
            string path = ShortcutPath();
            object link = null;
            int hr = 0;
            try
            {
                link = new ShellLink();
                ((IShellLinkW)link).SetPath(executable);
                ((IShellLinkW)link).SetDescription("LeGo My Cursor settings");
                ((IPersistFile)link).Save(path, true);
            }
            catch (Exception e) { hr = e.HResult; }
            finally
            {
                if (link != null)
                    Marshal.ReleaseComObject(link);
            }
            Log("Create Start menu shortcut: 0x" + hr.ToString("x8"));
            if (hr != 0)
                throw Fail("Create shortcut", hr);
        }

        internal static void RemoveShortcut()
        {
            try
            {
                File.Delete(ShortcutPath()); // A missing shortcut is already removed.
            }
            catch (Exception e) { throw Fail("Remove shortcut", Code(e)); }
        }

        internal static string Sha256(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        // Smart App Control gates WUDFHost loads on the ISG cloud verdict for the DLL's hash/signing information,
        // caching "known good" in .PURGE.ESBCACHE for 24 h. On the Go (2026-10-26), five same-source builds passed;
        // one failed (CodeIntegrity 3033/3077) across devnode restarts, then passed after rebuilding with a new hash.
        // A blocked DLL leaves installation successful and the devnode OK, but without the filter. LoadLibrary here
        // gets the same verdict, catching error 4551 (ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION) before any changes.
        internal static int Probe(string dll)
        {
            string hash = null;
            try
            {
                using (var file = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
                    hash = Sha256(file);
            }
            catch (Exception e) { Fail("SHA256", Code(e)); }
            IntPtr module = LoadLibraryEx(dll, IntPtr.Zero, DONT_RESOLVE_DLL_REFERENCES);
            int error = module == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (module != IntPtr.Zero)
                FreeLibrary(module);
            Log("DLL SHA-256=" + (hash ?? "unavailable") + "; probe=" +
                (error == 0 ? "loaded"
                    : error == ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION ? "Smart App Control blocked"
                    : "load failed") +
                "; Win32=" + error + " (0x" + error.ToString("x8") + ")");
            return error;
        }

        internal static string ExePath()
        {
            using (var process = Process.GetCurrentProcess())
                return process.MainModule.FileName;
        }

        internal static string InstallDirectory()
        {
            return Folder(Environment.SpecialFolder.ProgramFiles, "LeGo My Cursor");
        }

        internal static bool SameFile(string first, string second)
        {
            string a = Canonical(first), b = Canonical(second);
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string Canonical(string path)
        {
            // Zero access opens the file for metadata only.
            using (var file = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero))
            {
                if (file.IsInvalid)
                    return null;
                var buffer = new StringBuilder(LongPathCapacity);
                uint count = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
                return count > 0 && count < buffer.Capacity ? buffer.ToString() : null;
            }
        }

        internal static void RemoveInstalledFiles()
        {
            Stage("Removing application files…");
            string directory = InstallDirectory();
            try
            {
                File.Delete(Path.Combine(directory, "LeGoMyCursor.Setup.exe"));
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception e) { throw new Win32Exception(Code(e)); }
            int error = 0;
            try
            {
                Directory.Delete(directory);
            }
            catch (Exception e) { error = Code(e); }
            Log("Remove Program Files directory: " + error);
            if (error != 0 && error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND)
                throw new Win32Exception(error);
        }

        internal static void RemoveSettings(bool finalRemoval)
        {
            if (!finalRemoval)
                return; // Preserve settings during reinstall.
            Stage("Removing saved settings…");
            int status = DeleteTree(ParametersKey);
            Log("Remove saved settings: " + status);
            if (status != 0 && status != ERROR_FILE_NOT_FOUND && status != ERROR_PATH_NOT_FOUND)
                throw new Win32Exception(status);
        }

        // Returns whether Mode is stored; mode defaults to Simple and, like the driver, clamps values above Compliant.
        internal static bool ReadMode(out uint mode)
        {
            mode = ModeSimple;
            object value;
            try
            {
                using (var hklm = Hklm())
                using (var key = hklm.OpenSubKey(ParametersKey))
                    value = key?.GetValue("Mode");
            }
            catch (Exception e) { throw Fail("Read Mode", Code(e)); }
            if (value == null)
                return false;
            if (!(value is int))
                throw Fail("Read Mode", ERROR_UNSUPPORTED_TYPE); // As RegGetValue reports.
            mode = Math.Min(unchecked((uint)(int)value), (uint)ModeCompliant);
            return true;
        }

        internal static void WriteMode(uint mode)
        {
            if (mode > ModeCompliant)
                throw Fail("Write Mode", ERROR_INVALID_PARAMETER);
            int status = 0;
            try
            {
                using (var hklm = Hklm())
                using (var key = hklm.CreateSubKey(ParametersKey))
                    key.SetValue("Mode", (int)mode, RegistryValueKind.DWord);
            }
            catch (Exception e) { status = Code(e); }
            Log(@"Write HKLM\" + ParametersKey + @"\Mode=" + mode + ": 0x" + status.ToString("x8"));
            if (status != 0)
                throw Fail("Write Mode", status);
        }

        // Returns true when a temporary copy was launched to remove the installed executable; false when not needed.
        internal static bool Handoff(string action)
        {
            string source = ExePath();
            if (!SameFile(source, Path.Combine(InstallDirectory(), "LeGoMyCursor.Setup.exe")))
                return false;
            string directory = CreateWorkDirectory();
            string executable = Path.Combine(directory, "LeGoMyCursor.Setup.exe");
            int error = 0;
            try
            {
                File.Copy(source, executable, false);
                int pid;
                using (var current = Process.GetCurrentProcess())
                    pid = current.Id;
                var start = new ProcessStartInfo(executable, action + " /confirmed /temporary /parent:" + pid)
                {
                    UseShellExecute = false,
                    WorkingDirectory = directory
                };
                Process.Start(start).Dispose(); // The launched remover now owns its temporary files.
            }
            catch (Exception e)
            {
                error = Code(e);
                try
                {
                    File.Delete(executable);
                    Directory.Delete(directory);
                }
                catch { } // Best effort.
            }
            Log("Temporary remover handoff: " + error);
            if (error != 0)
                throw new Win32Exception(error);
            return true;
        }

        internal static int EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(new IntPtr(CurrentProcessHandle), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return Marshal.GetLastWin32Error();
            try
            {
                var privileges = new TokenPrivileges { Count = 1, Attributes = SE_PRIVILEGE_ENABLED };
                if (!LookupPrivilegeValue(null, name, out privileges.Luid))
                    return Marshal.GetLastWin32Error();
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
                return Marshal.GetLastWin32Error(); // Sets ERROR_NOT_ALL_ASSIGNED too.
            }
            finally { CloseHandle(token); }
        }

        internal static int RequestRestart()
        {
            int error = EnablePrivilege("SeShutdownPrivilege");
            if (error == 0)
            {
                error = InitiateShutdown(null, null, 0, SHUTDOWN_RESTART,
                    SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_INSTALLATION | SHTDN_REASON_FLAG_PLANNED);
            }
            Log("InitiateShutdownW: " + error);
            return error;
        }

        internal static void TrustCertificate(X509Certificate2 certificate, out byte[] thumbprint)
        {
            thumbprint = certificate.GetCertHash();
            // Add only the encoded public certificate: X509Store.Add would also copy the ephemeral key's properties.
            using (var publicOnly = new X509Certificate2(certificate.RawData))
            {
                foreach (string name in Stores)
                {
                    int error = 0;
                    try
                    {
                        using (var store = new X509Store(name, StoreLocation.LocalMachine))
                        {
                            store.Open(OpenFlags.ReadWrite);
                            store.Add(publicOnly);
                        }
                    }
                    catch (Exception e) { error = Code(e); }
                    Log(@"Trust public certificate LocalMachine\" + name + ": 0x" + error.ToString("x8"));
                    if (error == 0)
                        continue;
                    try
                    {
                        RemoveCertificate(thumbprint);
                    }
                    catch (Win32Exception) { } // Rollback is best effort; report the trust failure.
                    throw Fail("Trust signer certificate", error);
                }
            }
        }

        internal static void RemoveCertificate(byte[] thumbprint)
        {
            string hex = BitConverter.ToString(thumbprint).Replace("-", "");
            int first = 0;
            foreach (string name in Stores)
            {
                int error = 0;
                try
                {
                    using (var store = new X509Store(name, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadWrite);
                        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, hex, false);
                        foreach (X509Certificate2 found in matches)
                            store.Remove(found);
                    }
                }
                catch (Exception e) { error = Code(e); }
                Log(@"Remove certificate LocalMachine\" + name + ": 0x" + error.ToString("x8"));
                if (first == 0)
                    first = error;
            }
            if (first != 0)
                throw Fail("Remove signer certificate", first);
        }

        internal static void RemoveSignerCertificates()
        {
            byte[] subject = new X500DistinguishedName(Subject).RawData;
            int first = 0;
            foreach (string name in Stores)
            {
                try
                {
                    using (var store = new X509Store(name, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadWrite);
                        foreach (X509Certificate2 certificate in store.Certificates)
                        {
                            if (!certificate.SubjectName.RawData.SequenceEqual(subject))
                                continue; // Exact encoded-subject match only.
                            try
                            {
                                store.Remove(certificate);
                                Log("Removed exact-subject signer from " + name);
                            }
                            catch (Exception e)
                            {
                                if (first == 0)
                                    first = Code(e);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    int error = Code(e);
                    if (first == 0)
                        first = error;
                    Log("Open " + name + ": 0x" + error.ToString("x8"));
                }
            }
            if (first != 0)
                throw Fail("Clean signer certificates", first);
        }

        // Signs with a new self-signed certificate whose ephemeral key dies with it. The caller owns the certificate.
        internal static X509Certificate2 SignCatalog(string catalog)
        {
            X509Certificate2 certificate;
            try
            {
                using (var key = new RSACng(2048))
                {
                    var request =
                        new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var codeSigning = new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") };
                    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(codeSigning, false));
                    certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                        new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero));
                }
            }
            catch (CryptographicException e) { throw Fail("Create self-signed certificate", Code(e)); }
            var memory = new List<IntPtr>();
            IntPtr Put(object value)
            {
                memory.Add(Marshal.AllocHGlobal(Marshal.SizeOf(value)));
                Marshal.StructureToPtr(value, memory[memory.Count - 1], false);
                return memory[memory.Count - 1];
            }
            int hr;
            try
            {
                memory.Add(Marshal.StringToHGlobalUni(catalog));
                var file = new SignerFileInfo { Size = Marshal.SizeOf(typeof(SignerFileInfo)), FileName = memory[0] };
                var subject = new SignerSubjectInfo
                {
                    Size = Marshal.SizeOf(typeof(SignerSubjectInfo)),
                    Index = Put(new SignerIndex()),
                    Choice = SIGNER_SUBJECT_FILE,
                    FileInfo = Put(file)
                };
                var store = new SignerCertStoreInfo
                {
                    Size = Marshal.SizeOf(typeof(SignerCertStoreInfo)),
                    SigningCert = certificate.Handle,
                    Policy = SIGNER_CERT_POLICY_CHAIN
                };
                var signer = new SignerCert
                {
                    Size = Marshal.SizeOf(typeof(SignerCert)),
                    Choice = SIGNER_CERT_STORE,
                    StoreInfo = Put(store)
                };
                var signature = new SignerSignatureInfo
                {
                    Size = Marshal.SizeOf(typeof(SignerSignatureInfo)),
                    HashAlgorithm = CALG_SHA_256
                };
                IntPtr context;
                hr = SignerSignEx2(0, ref subject, ref signer, ref signature, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, out context, IntPtr.Zero, IntPtr.Zero);
                if (context != IntPtr.Zero)
                    SignerFreeSignerContext(context);
            }
            finally
            {
                foreach (IntPtr block in memory)
                    Marshal.FreeHGlobal(block);
            }
            Log("SignerSignEx2 catalog-only SHA256: 0x" + hr.ToString("x8"));
            if (hr >= 0)
                return certificate;
            certificate.Dispose();
            throw Fail("Sign catalog", hr);
        }

        // Active when every touchpad bound to oem runs under WUDFRd and a WUDFHost loaded that package's DLL.
        // The default retries let a just-installed or restarted touchpad settle.
        internal static ActiveState Verify(string oem, int attempts = VerifyAttempts)
        {
            var location = new StringBuilder(LongPathCapacity);
            if (!SetupGetInfDriverStoreLocation(oem, IntPtr.Zero, null, location, location.Capacity, IntPtr.Zero))
            {
                Fail("Resolve installed driver-store package", Marshal.GetLastWin32Error());
                return ActiveState.Inactive;
            }
            int slash = location.ToString().LastIndexOf('\\');
            if (slash < 0)
            {
                Fail("Resolve installed DLL", ERROR_INVALID_NAME);
                return ActiveState.Inactive;
            }
            string dll = location.ToString(0, slash + 1) + "legomycursor.dll", expected = Leaf(oem);
            Log("Enable debug privilege for WUDFHost verification: 0x" +
                EnablePrivilege("SeDebugPrivilege").ToString("x8"));
            for (int attempt = 1; ; ++attempt)
            {
                int touchpads = 0, bound = 0;
                bool stacks = true, enumerated = true;
                try
                {
                    Devices((set, device) =>
                    {
                        if (!HasId(Property(set, device, HardwareIds), Touchpad))
                            return;
                        ++touchpads;
                        string inf = Property(set, device, DriverInfPath).FirstOrDefault() ?? "";
                        if (!string.Equals(inf, expected, StringComparison.OrdinalIgnoreCase))
                            return;
                        ++bound;
                        bool wudf = HasId(Property(set, device, DeviceStack), @"\DRIVER\WUDFRD");
                        stacks &= wudf;
                        Log("Verify device INF=" + inf + " WUDFRd=" + (wudf ? 1 : 0));
                    });
                }
                // Devices logged the failure; the tallies cover the devices visited.
                catch (Win32Exception) { enumerated = false; }
                bool loaded = bound > 0 && DriverModuleLoaded(dll);
                Log("Verify attempt " + attempt + ": touchpads=" + touchpads + " bound=" + bound +
                    " stack=" + (stacks ? 1 : 0) + " module=" + (loaded ? 1 : 0) +
                    " enumeration=" + (enumerated ? 1 : 0));
                if (enumerated && bound > 0 && stacks && loaded)
                    return ActiveState.Active;
                if (attempt >= attempts)
                    return enumerated && bound == 0 ? ActiveState.NoDevice : ActiveState.Inactive;
                Thread.Sleep(VerifyRetryMilliseconds);
            }
        }

        private static string Leaf(string path) { return path.Substring(path.LastIndexOf('\\') + 1); }

        private static bool DriverModuleLoaded(string dll)
        {
            var pids = new int[1024];
            int bytes;
            for (; ; pids = new int[pids.Length * 2])
            {
                if (!EnumProcesses(pids, pids.Length * sizeof(int), out bytes))
                {
                    Fail("EnumProcesses", Marshal.GetLastWin32Error());
                    return false;
                }
                if (bytes < pids.Length * sizeof(int))
                    break;
            }
            foreach (int pid in pids.Take(bytes / sizeof(int)))
            {
                if (!IsHost(pid))
                    continue;
                IntPtr process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
                if (process == IntPtr.Zero)
                {
                    Log("Open WUDFHost " + pid + " modules: 0x" + Marshal.GetLastWin32Error().ToString("x8"));
                    continue;
                }
                try
                {
                    var modules = new IntPtr[256];
                    int needed;
                    bool ok = EnumProcessModulesEx(process, modules, modules.Length * IntPtr.Size, out needed,
                        LIST_MODULES_ALL);
                    if (ok && needed > modules.Length * IntPtr.Size)
                    {
                        modules = new IntPtr[needed / IntPtr.Size];
                        ok = EnumProcessModulesEx(process, modules, modules.Length * IntPtr.Size, out needed,
                            LIST_MODULES_ALL);
                    }
                    if (!ok)
                    {
                        Log("Enumerate WUDFHost " + pid + " modules: 0x" + Marshal.GetLastWin32Error().ToString("x8"));
                        continue;
                    }
                    var name = new StringBuilder(MAX_PATH);
                    foreach (IntPtr module in modules.Take(needed / IntPtr.Size))
                    {
                        if (GetModuleFileNameEx(process, module, name, name.Capacity) > 0 &&
                            string.Equals(Leaf(name.ToString()), "legomycursor.dll",
                                StringComparison.OrdinalIgnoreCase) &&
                            SameFile(name.ToString(), dll))
                        {
                            Log(name + " loaded in WUDFHost PID " + pid);
                            return true;
                        }
                    }
                }
                finally { CloseHandle(process); }
            }
            return false;
        }

        private static bool IsHost(int pid)
        {
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
                return false;
            try
            {
                var image = new StringBuilder(MAX_PATH);
                int length = image.Capacity;
                return QueryFullProcessImageName(process, 0, image, ref length) &&
                    string.Equals(Leaf(image.ToString()), "WUDFHost.exe", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseHandle(process); }
        }

        internal const int MAX_PATH = 260;
        private const int MAX_DEVICE_ID_LEN = 200;
        private const int LongPathCapacity = 32768; // Extended-length path limit.
        private const int HKEY_LOCAL_MACHINE = unchecked((int)0x80000002);
        private const int INVALID_HANDLE_VALUE = -1;
        private const int CurrentProcessHandle = -1; // GetCurrentProcess() pseudo-handle.
        private const uint INVALID_FILE_ATTRIBUTES = uint.MaxValue;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint DONT_RESOLVE_DLL_REFERENCES = 0x1;
        private const uint SDDL_REVISION_1 = 1;
        internal const uint SPOST_PATH = 1;
        internal const uint SP_COPY_NOOVERWRITE = 0x8;
        private const uint INFINFO_INF_NAME_IS_ABSOLUTE = 2;
        private const uint DIGCF_PRESENT = 0x2;
        private const uint DIGCF_ALLCLASSES = 0x4;
        private const uint DEVPROP_TYPE_STRING = 0x12;
        private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;
        private const int SP_CLASSINSTALL_HEADER_SIZE = 8;
        private const int DIF_PROPERTYCHANGE = 0x12;
        private const int DICS_PROPCHANGE = 3;
        private const int DICS_FLAG_GLOBAL = 1;
        private const uint TOKEN_QUERY = 0x8;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        private const int SE_PRIVILEGE_ENABLED = 0x2;
        private const uint SHUTDOWN_RESTART = 0x4;
        private const uint SHTDN_REASON_MAJOR_APPLICATION = 0x00040000;
        private const uint SHTDN_REASON_MINOR_INSTALLATION = 0x00000002;
        private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;
        private const uint PROCESS_VM_READ = 0x10;
        private const uint PROCESS_QUERY_INFORMATION = 0x400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint LIST_MODULES_ALL = 0x3;
        private const int SIGNER_SUBJECT_FILE = 1;
        private const int SIGNER_CERT_STORE = 2;
        // For a self-signed signer, CHAIN and CHAIN_NO_ROOT (8) produce byte-identical catalogs that embed the signer.
        private const int SIGNER_CERT_POLICY_CHAIN = 2;
        private const int CALG_SHA_256 = 0x800C;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OriginalFileInfo
        {
            internal int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)] internal string InfName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)] internal string CatalogName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevInfoData
        {
            internal int Size;
            internal Guid ClassGuid;
            internal int DevInst;
            internal IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey
        {
            internal Guid Category;
            internal int Id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropChangeParams
        {
            internal int HeaderSize, InstallFunction, StateChange, Scope, HwProfile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr Descriptor;
            internal int Inherit;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TokenPrivileges
        {
            internal int Count;
            internal long Luid;
            internal int Attributes;
        }

        // mssign32 SIGNER_* structures, x64 layout as the former native backend declared them.
        [StructLayout(LayoutKind.Sequential)]
        private struct SignerFileInfo
        {
            internal int Size;
            internal IntPtr FileName, File;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SignerIndex
        {
            internal int Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SignerSubjectInfo
        {
            internal int Size;
            internal IntPtr Index;
            internal int Choice;
            internal IntPtr FileInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SignerCertStoreInfo
        {
            internal int Size;
            internal IntPtr SigningCert;
            internal int Policy;
            internal IntPtr Store;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SignerCert
        {
            internal int Size, Choice;
            internal IntPtr StoreInfo, Window;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SignerSignatureInfo
        {
            internal int Size, HashAlgorithm, AttrChoice;
            internal IntPtr Authcode, Authenticated, Unauthenticated;
        }

        // Only SetDescription and SetPath are called; the other slots keep the vtable order.
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath();
            void GetIDList();
            void SetIDList();
            void GetDescription();
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            void GetWorkingDirectory();
            void SetWorkingDirectory();
            void GetArguments();
            void SetArguments();
            void GetHotkey();
            void SetHotkey();
            void GetShowCmd();
            void SetShowCmd();
            void GetIconLocation();
            void SetIconLocation();
            void SetRelativePath();
            void Resolve();
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DiInstallDriverW")]
        internal static extern bool DiInstallDriver(IntPtr parent, string inf, uint flags, out bool reboot);
        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DiUninstallDriverW")]
        internal static extern bool DiUninstallDriver(IntPtr parent, string inf, uint flags, out bool reboot);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupCopyOEMInfW")]
        internal static extern bool SetupCopyOEMInf(string source, string media, uint mediaType, uint style,
            StringBuilder destination, int size, IntPtr required, IntPtr component);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupGetInfInformationW")]
        private static extern bool SetupGetInfInformation(string inf, uint search, byte[] info, int size,
            out int required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupQueryInfOriginalFileInformationW")]
        private static extern bool SetupQueryInfOriginalFileInformation(byte[] info, uint index, IntPtr platform,
            ref OriginalFileInfo original);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent,
            uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref DevInfoData device);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
        private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DevInfoData device, ref DevPropKey key,
            out uint type, byte[] buffer, int size, out int required, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupDiGetDeviceInstanceIdW")]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DevInfoData device, StringBuilder id,
            int size, IntPtr required);
        [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiSetClassInstallParamsW")]
        private static extern bool SetupDiSetClassInstallParams(IntPtr set, ref DevInfoData device,
            ref PropChangeParams parameters, int size);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DevInfoData device);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupGetInfDriverStoreLocationW")]
        private static extern bool SetupGetInfDriverStoreLocation(string inf, IntPtr platform, string locale,
            StringBuilder location, int size, IntPtr required);
        // DefaultDllImportSearchPaths(System32) loads mssign32 with LOAD_LIBRARY_SEARCH_SYSTEM32,
        // as the native backend did.
        [DllImport("mssign32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SignerSignEx2(int flags, ref SignerSubjectInfo subject, ref SignerCert cert,
            ref SignerSignatureInfo signature, IntPtr provider, int timestampFlags, IntPtr timestampOid,
            IntPtr timestampUrl, IntPtr request, IntPtr sipData, out IntPtr context, IntPtr policy, IntPtr reserved);
        [DllImport("mssign32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SignerFreeSignerContext(IntPtr context);
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcesses([Out] int[] pids, int size, out int returned);
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size,
            out int needed, uint filter);
        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetModuleFileNameExW")]
        private static extern int GetModuleFileNameEx(IntPtr process, IntPtr module, StringBuilder name, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")]
        private static extern uint GetFileAttributes(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DeleteFileW")]
        internal static extern bool DeleteFile(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image,
            ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
        private static extern bool CreateDirectory(string path, ref SecurityAttributes security);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "GetFinalPathNameByHandleW")]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size,
            uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryExW")]
        internal static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll")]
        internal static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision,
            out IntPtr descriptor, IntPtr size);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegDeleteTreeW")]
        private static extern int RegDeleteTree(IntPtr key, string subkey);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "LookupPrivilegeValueW")]
        private static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state,
            uint length, IntPtr previous, IntPtr returned);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "InitiateShutdownW")]
        private static extern int InitiateShutdown(string machine, string message, uint grace, uint flags, uint reason);
    }
}
