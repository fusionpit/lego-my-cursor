using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LeGoMyCursor.Setup
{
    internal sealed class InstallerOptions
    {
        internal SetupOperation? Operation;
        internal int Mode = Platform.ModeSimple;
        internal bool Confirmed, Temporary, Headless;
        internal uint Parent;

        private const string ParentPrefix = "/parent:";
        private const string ModePrefix = "/mode:";
        private const uint ParentTimeoutMilliseconds = 30000;

        internal static InstallerOptions Parse(string[] args)
        {
            var options = new InstallerOptions();
            bool actionSet = false;
            foreach (string original in args)
            {
                string argument = original.ToLowerInvariant();
                if (argument == "/confirmed")
                {
                    options.Confirmed = true;
                    continue;
                }
                if (argument == "/temporary")
                {
                    options.Temporary = true;
                    continue;
                }
                if (argument.StartsWith(ParentPrefix, StringComparison.Ordinal))
                {
                    if (!uint.TryParse(argument.Substring(ParentPrefix.Length), out options.Parent) ||
                        options.Parent == 0)
                        throw Invalid();
                    continue;
                }
                if (actionSet)
                    throw Invalid();
                actionSet = true;
                if (argument.StartsWith(ModePrefix, StringComparison.Ordinal))
                {
                    string value = argument.Substring(ModePrefix.Length);
                    if (value != "0" && value != "1" && value != "2")
                        throw Invalid();
                    options.Operation = SetupOperation.ApplyMode;
                    options.Mode = value[0] - '0';
                    options.Headless = true;
                    continue;
                }
                switch (argument)
                {
                    case "/probe":
                        options.Operation = SetupOperation.Probe;
                        options.Headless = true;
                        break;
                    case "/reinstall":
                        options.Operation = SetupOperation.Reinstall;
                        options.Headless = true;
                        break;
                    case "/uninstall":
                        options.Operation = SetupOperation.Uninstall;
                        break;
                    case "/clean":
                        options.Operation = SetupOperation.Clean;
                        break;
                    default:
                        throw Invalid();
                }
            }
            if (options.Temporary &&
                (options.Parent == 0 ||
                    (options.Operation != SetupOperation.Uninstall && options.Operation != SetupOperation.Clean)))
            {
                throw Invalid();
            }
            if ((options.Operation == SetupOperation.ApplyMode || options.Operation == SetupOperation.Reinstall) &&
                (options.Confirmed || options.Temporary || options.Parent != 0))
                throw Invalid();
            return options;
        }

        private static ArgumentException Invalid()
        {
            return new ArgumentException(
                "Run setup without arguments, or use /probe, /reinstall, /mode:0|1|2, /uninstall, or /clean.");
        }

        internal void WaitForParent()
        {
            if (!Temporary)
                return;
            IntPtr parent = OpenProcess(SYNCHRONIZE, false, Parent);
            if (parent != IntPtr.Zero)
            {
                try
                {
                    if (WaitForSingleObject(parent, ParentTimeoutMilliseconds) != WAIT_OBJECT_0)
                    {
                        throw new InvalidOperationException(
                            "The parent installer has not closed. Close it and retry removal.");
                    }
                }
                finally { CloseHandle(parent); }
            }
            else if (Marshal.GetLastWin32Error() != Platform.ERROR_INVALID_PARAMETER) // The parent already exited.
                throw new Win32Exception();
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            if (!MoveFileEx(executable, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                throw new Win32Exception();
            MoveFileEx(Path.GetDirectoryName(executable), null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }

        private const uint SYNCHRONIZE = 0x00100000;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string destination, uint flags);
    }
}
