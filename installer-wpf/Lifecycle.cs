using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LeGoMyCursor.Setup
{
    internal enum SetupOperation { Probe = 1, Install = 2, Reinstall = 3, ApplyMode = 4, Uninstall = 5, Clean = 6 }
    internal enum OutcomeKind { General, Installed, Mode, Removed, Cleaned, InstalledRestart }

    internal sealed class Outcome
    {
        internal bool Ok, ModeActive, Installed, Reboot;
        internal string Title, Detail, Warning = "";
        internal OutcomeKind Kind;
        internal uint Mode = Platform.ModeSimple;
        // Set by Lifecycle.Finish; a successful Query leaves them at Success and exit code 0.
        internal ResultKind Status;
        internal int ExitCode;

        internal Outcome(bool ok, string title, string detail, OutcomeKind kind = OutcomeKind.General)
        {
            Ok = ok;
            Title = title;
            Detail = detail;
            Kind = kind;
        }
    }

    // Install, mode, and removal flows.
    internal sealed class Lifecycle
    {
        private const string NotInstalled = " The fix is now not installed. Run setup again to recover.";
        private const string RunClean = "Run LeGoMyCursor.Setup.exe /clean from an administrator prompt";
        internal bool RebootRequired; // Accumulates across operations in this process.

        private static string ErrorText(string step, int error)
        {
            Platform.Log(step + " failed: Win32 " + error + " (0x" + error.ToString("X8") + ")");
            return step + " failed (Win32 " + error + "). Open the log folder for details.";
        }

        private static string ErrorText(string step, Exception error) { return ErrorText(step, Platform.Code(error)); }

        private static bool Succeeds(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception) { return false; } // The helpers log their own failures.
        }

        private static int Bit(bool value) { return value ? 1 : 0; }

        private static Outcome RecordError(Exception error)
        {
            return new Outcome(false, "The install record could not be read",
                RunClean + ", then run setup again. " + ErrorText("Reading the install record", error));
        }

        // Returns a failure when the driver store holds a LeGo My Cursor package that no install record owns.
        private static Outcome Unrecorded()
        {
            List<string> skipped;
            try
            {
                if (Platform.FindPackages(out skipped).Count == 0)
                    return null;
            }
            catch (Exception e)
            {
                return new Outcome(false, "The driver store could not be read",
                    ErrorText("Enumerating driver packages", e));
            }
            return new Outcome(false, "An unrecorded driver is installed",
                "A LeGo My Cursor driver is installed that this installer didn't record. " +
                RunClean + ", then run setup again.");
        }

        // Returns the /probe exit code: the LoadLibraryEx error for the driver DLL, or 1 if extraction failed.
        internal static int Probe()
        {
            Platform.Stage("Checking the driver package…");
            string payload;
            try
            {
                payload = Platform.ExtractPayload();
            }
            catch (Exception e)
            {
                ErrorText("Extracting the driver package", e);
                return 1;
            }
            try
            {
                int error = Platform.Probe(Path.Combine(payload, "legomycursor.dll"));
                Platform.Log("Final outcome: probe Win32 " + error + "; no installation changes");
                return error;
            }
            finally { Platform.DeletePayload(payload); }
        }

        internal Outcome Query()
        {
            Record record;
            try
            {
                record = Record.Read();
            }
            catch (Exception e) { return Finish(RecordError(e)); }
            uint mode = Platform.ModeSimple;
            if (record.Present)
            {
                try
                {
                    Platform.ReadMode(out mode);
                }
                catch (Exception e)
                {
                    return Finish(new Outcome(false, "Could not read fix mode", ErrorText("Reading the fix mode", e)));
                }
            }
            else
            {
                var unrecorded = Unrecorded();
                if (unrecorded != null)
                    return Finish(unrecorded);
            }
            return new Outcome(true, "", "")
            {
                Installed = record.Present,
                Mode = mode
            };
        }

        // The configuration page's status line. Never throws; Verify logs its own details.
        internal static string ActiveStatus()
        {
            try
            {
                var record = Record.Read();
                if (record.Present)
                {
                    var active = Platform.Verify(record.Oem, 1); // Nothing just changed; no settle retries.
                    return active == ActiveState.Active ? "Status: Active"
                        : active == ActiveState.NoDevice ? "Status: No Legion Go touchpad found"
                        : "Status: Inactive";
                }
            }
            catch (Exception e) { Platform.Log("Status check failed: " + e); }
            return "Status: Unknown";
        }

        internal Outcome Execute(SetupOperation operation, uint mode)
        {
            var outcome = Run(operation, mode);
            if ((operation == SetupOperation.Install || operation == SetupOperation.Reinstall) && outcome.Ok)
                outcome.Installed = true;
            Finish(outcome);
            Platform.Log("Final outcome: " + outcome.Title + "; " + outcome.Detail +
                "; reboot required=" + Bit(RebootRequired));
            return outcome;
        }

        private Outcome Run(SetupOperation operation, uint mode)
        {
            if (operation == SetupOperation.Install)
                return InstallSelectedMode(mode);
            if (operation == SetupOperation.Clean)
                return Clean();
            if (operation == SetupOperation.Uninstall)
                return UninstallRecorded();
            Record record;
            try
            {
                record = Record.Read();
            }
            catch (Exception e) { return RecordError(e); }
            if (!record.Present)
            {
                return new Outcome(false, "No installation was recorded",
                    "Install the driver before changing its mode.");
            }
            return operation == SetupOperation.Reinstall ? Install(record) : ApplyMode(mode);
        }

        private Outcome Finish(Outcome outcome)
        {
            outcome.Reboot = RebootRequired;
            outcome.Status = !outcome.Ok ? ResultKind.Failure
                : SuccessfulCompletion(outcome, RebootRequired) ? ResultKind.Success
                : ResultKind.Warning;
            outcome.ExitCode = !outcome.Ok ? Platform.ERROR_INSTALL_FAILURE
                : RebootRequired ? Platform.ERROR_SUCCESS_REBOOT_REQUIRED
                : 0;
            return outcome;
        }

        internal static bool SuccessfulCompletion(Outcome outcome, bool reboot)
        {
            return outcome.Ok && !reboot &&
                (outcome.ModeActive || outcome.Kind == OutcomeKind.Removed || outcome.Kind == OutcomeKind.Cleaned);
        }

        internal static bool RestartComplete(RestartResult result)
        {
            return result.Error == 0 && result.Total != 0 && result.Restarted == result.Total;
        }

        internal static string RestartFooter(RestartResult result)
        {
            if (result.Error == 0 && result.Total == 0)
            {
                return "The selected mode was saved, but no connected touchpad was found. " +
                    "It will take effect when the touchpad next starts.";
            }
            if (RestartComplete(result))
                return "Applied.";
            Platform.Log("Mode saved; restart incomplete: " + result.Restarted + " of " + result.Total +
                ", Win32 " + result.Error);
            return "The selected mode was saved, but the touchpad restart did not complete. " +
                "Restart Windows to finish applying it.\r\n\r\n" +
                result.Restarted + " of " + result.Total + " touchpad devices restarted (Win32 " + result.Error + ").";
        }

        internal static bool ModeIsActive(bool applied, ActiveState active, bool reboot)
        {
            return applied && active == ActiveState.Active && !reboot;
        }

        internal static string RestartContext(Outcome outcome)
        {
            if (outcome.Warning.Length == 0 &&
                (SuccessfulCompletion(outcome, false) || (outcome.Ok && outcome.Kind == OutcomeKind.InstalledRestart)))
            {
                return "";
            }
            string context = outcome.Title;
            if (outcome.Detail.Length > 0)
                context += "\r\n\r\n" + outcome.Detail;
            if (outcome.Warning.Length > 0)
                context += "\r\n\r\n" + outcome.Warning;
            return context;
        }

        private Outcome ApplyMode(uint mode)
        {
            var result = new Outcome(false, "Fix mode", "", OutcomeKind.Mode) { Mode = mode };
            Platform.Stage("Saving your fix mode…");
            try
            {
                Platform.WriteMode(mode);
            }
            catch (Exception e)
            {
                result.Detail = ErrorText("Saving the fix mode", e);
                return result;
            }
            Platform.Stage("Restarting your touchpad…");
            var restart = Platform.RestartTouchpads();
            result.Detail = RestartFooter(restart);
            if (!RestartComplete(restart))
                return result;
            if (RebootRequired)
            {
                result.Detail = "The selected mode was saved. Restart Windows to finish changing the driver.";
                return result;
            }
            Record record;
            try
            {
                record = Record.Read();
            }
            catch (Exception) { record = new Record(); }
            if (!record.Present)
            {
                result.Detail = "The selected mode was saved, but the installed driver record could not be verified. " +
                    "Open the log folder for details.";
                return result;
            }
            Platform.Stage("Checking your touchpad…");
            var activation = Platform.Verify(record.Oem);
            result.Ok = result.ModeActive = activation == ActiveState.Active;
            if (!result.ModeActive)
            {
                result.Detail = activation == ActiveState.NoDevice
                    ? "The selected mode was saved, but no Legion Go touchpad is bound to the installed driver."
                    : "The selected mode was saved, but the touchpad fix could not be verified as active. " +
                        "Restart Windows and run setup again.";
            }
            return result;
        }

        private static string LookupOem(string inf, out int error)
        {
            var path = new StringBuilder(Platform.MAX_PATH);
            bool ok = Platform.SetupCopyOEMInf(inf, null, Platform.SPOST_PATH, Platform.SP_COPY_NOOVERWRITE,
                path, path.Capacity, IntPtr.Zero, IntPtr.Zero);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            Platform.Log("SetupCopyOEMInfW(NOOVERWRITE): " + error + " OEM=" + path);
            // ERROR_FILE_EXISTS: the package is already in the store, and path names it.
            if ((ok || error == Platform.ERROR_FILE_EXISTS) && Record.ValidOemName(path.ToString()))
                return path.ToString();
            if (error == 0)
                error = Platform.ERROR_INVALID_DATA;
            return null;
        }

        // previous.Present reinstalls (keeping the saved mode); requestedMode Simple..Compliant is a fresh install.
        internal Outcome Install(Record previous, int requestedMode = -1)
        {
            Platform.Stage("Preparing installation…");
            var result = new Outcome(false, "Installation failed", "", OutcomeKind.Installed);
            if (requestedMode != -1 &&
                (previous.Present || requestedMode < Platform.ModeSimple || requestedMode > Platform.ModeCompliant))
            {
                result.Detail = "A fresh installation requires Simple or Compliant and no existing install record. " +
                    "Close and run setup again.";
                return result;
            }
            Platform.Log("Install requested mode=" + requestedMode + "; previous record=" + Bit(previous.Present));
            Platform.Stage("Extracting driver files…");
            string payload;
            try
            {
                payload = Platform.ExtractPayload();
            }
            catch (Exception e)
            {
                result.Detail = ErrorText("Extracting the driver package", e);
                return result;
            }
            try
            {
                Platform.Stage("Checking the driver…");
                if (Platform.Probe(Path.Combine(payload, "legomycursor.dll")) ==
                    Platform.ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION)
                {
                    result.Title = "Smart App Control blocked the LeGo My Cursor driver";
                    result.Detail = "Windows Smart App Control blocked this driver. " +
                        "Your existing installation has not been changed. " +
                        "Review Smart App Control in Windows Security > App & browser control.";
                    return result;
                }
                Platform.Stage("Preparing driver…");
                X509Certificate2 certificate;
                try
                {
                    certificate = Platform.SignCatalog(Path.Combine(payload, "legomycursor.cat"));
                }
                catch (Exception e)
                {
                    result.Detail = ErrorText("Signing the driver catalog", e);
                    return result;
                }
                using (certificate)
                    return InstallPayload(previous, requestedMode, payload, certificate, result);
            }
            finally { Platform.DeletePayload(payload); }
        }

        private Outcome InstallPayload(Record previous, int requestedMode, string payload,
            X509Certificate2 certificate, Outcome result)
        {
            uint mode = requestedMode >= 0 ? (uint)requestedMode : Platform.ModeSimple;
            bool modePresent = requestedMode >= 0, removedPrevious = false;
            Func<List<string>, string, bool> known =
                (paths, path) => paths.Any(old => string.Equals(old, path, StringComparison.OrdinalIgnoreCase));
            // Scan before removal so scan failure leaves the driver installed.
            // Rollback preserves before/skippedBefore entries.
            List<string> before, skippedBefore;
            try
            {
                before = Platform.FindPackages(out skippedBefore);
            }
            catch (Exception e)
            {
                result.Detail = ErrorText("Enumerating the driver store", e);
                return result;
            }
            if (skippedBefore.Count > 0)
                Platform.Log("Warning: " + skippedBefore.Count + " driver store entries could not be read");
            // pnputil /add-driver recreates the UMDF service key, deleting Parameters. Save Mode before installation,
            // restore it afterward, then restart touchpads again: the driver initially starts with defaults and
            // reads Mode only at device start.
            if (previous.Present)
            {
                Platform.Stage("Reading your current fix mode…");
                try
                {
                    modePresent = Platform.ReadMode(out mode);
                }
                catch (Exception e)
                {
                    result.Detail = ErrorText("Reading the current fix mode", e);
                    return result;
                }
                string error = RemoveRecorded(previous, false);
                if (error != null)
                {
                    result.Detail = error;
                    return result;
                }
                removedPrevious = true;
                // A new package may reuse the removed slot; keep it eligible for rollback.
                before.RemoveAll(path => string.Equals(path, previous.Oem, StringComparison.OrdinalIgnoreCase));
            }
            byte[] thumbprint;
            Platform.Stage("Preparing driver…");
            try
            {
                Platform.TrustCertificate(certificate, out thumbprint);
            }
            catch (Exception e)
            {
                result.Detail = ErrorText("Trusting the local signing certificate", e) +
                    (removedPrevious ? NotInstalled : "");
                return result;
            }
            Func<bool> removeNewPackages = () =>
            {
                Platform.Stage("Checking installation cleanup…");
                List<string> after, skippedAfter;
                try
                {
                    after = Platform.FindPackages(out skippedAfter);
                }
                catch (Exception) { return false; }
                bool removed = true;
                foreach (string package in after)
                {
                    if (!known(before, package) && !known(skippedBefore, package) && RemovePackage(package) != 0)
                        removed = false;
                }
                foreach (string package in skippedAfter)
                {
                    if (!known(before, package) && !known(skippedBefore, package))
                    {
                        Platform.Log("Unreadable package " + package + " may be new; left in place");
                        removed = false;
                    }
                }
                return removed;
            };
            bool reboot;
            Platform.Stage("Installing driver…");
            bool installed =
                Platform.DiInstallDriver(IntPtr.Zero, Path.Combine(payload, "legomycursor.inf"), 0, out reboot);
            int installError = installed ? 0 : Marshal.GetLastWin32Error();
            RebootRequired |= reboot;
            Platform.Log("DiInstallDriverW: " + installError + " reboot=" + Bit(reboot) +
                " accumulated=" + Bit(RebootRequired));
            if (!installed)
            {
                bool packagesRemoved = removeNewPackages(); // A failed install can still have staged the package.
                bool certificateRemoved = Succeeds(() => Platform.RemoveCertificate(thumbprint));
                result.Detail = ErrorText("Installing the driver", installError);
                if (removedPrevious)
                    result.Detail += NotInstalled;
                if (!packagesRemoved || !certificateRemoved)
                    result.Detail += " Cleanup was incomplete. " + RunClean + ".";
                return result;
            }
            string oem = null;
            bool copied = false;
            Action<string> rollback = reason =>
            {
                Platform.Stage("Undoing installation changes…");
                bool removed = oem != null ? RemovePackage(oem) == 0 : removeNewPackages();
                bool certificateRemoved = Succeeds(() => Platform.RemoveCertificate(thumbprint));
                bool recordRemoved = Succeeds(Record.Delete);
                if (copied)
                {
                    Succeeds(() => Platform.RemoveShortcut());
                    if (!Succeeds(Platform.RemoveInstalledFiles))
                        removed = false;
                }
                Platform.Log("Rollback complete: driver/files=" + Bit(removed) +
                    " certificate=" + Bit(certificateRemoved) + " record=" + Bit(recordRemoved));
                result.Detail = reason + (removed && certificateRemoved && recordRemoved
                    ? NotInstalled
                    : " Cleanup was incomplete. " + RunClean + ", then run setup again.");
            };
            Platform.Stage("Finishing installation…");
            int lookupError;
            oem = LookupOem(Path.Combine(payload, "legomycursor.inf"), out lookupError);
            if (oem == null)
            {
                rollback(ErrorText("Locating the installed driver package", lookupError));
                return result;
            }
            string destination;
            try
            {
                destination = Path.Combine(Platform.InstallDirectory(), "LeGoMyCursor.Setup.exe");
                if (!Platform.SameFile(Platform.ExePath(), destination))
                {
                    Directory.CreateDirectory(Platform.InstallDirectory());
                    copied = true; // Rollback must also remove partial copies.
                    File.Copy(Platform.ExePath(), destination, true);
                    Platform.Log("Setup copied to " + destination);
                }
                else
                    Platform.Log("Setup already runs from Program Files; skipping same-file copy");
            }
            catch (Exception e)
            {
                rollback(ErrorText("Copying setup to Program Files", e));
                return result;
            }
            try
            {
                Record.Write(oem, thumbprint);
            }
            catch (Exception e)
            {
                rollback(ErrorText("Writing the Apps & Features record", e));
                return result;
            }
            string warning = "";
            try
            {
                Platform.CreateShortcut(destination);
            }
            catch (Exception e)
            {
                Platform.Log("Start menu shortcut failed: " + Platform.Code(e));
                warning = "The Start menu shortcut could not be created. " +
                    "Open settings through Apps & Features > LeGo My Cursor > Modify.";
            }
            // File.Copy keeps the download's Zone.Identifier stream, which makes SmartScreen re-check
            // Start menu launches.
            if (!Platform.DeleteFile(destination + ":Zone.Identifier"))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != Platform.ERROR_FILE_NOT_FOUND)
                {
                    Platform.Log("Removing Zone.Identifier from " + destination + " failed: " + error);
                    warning += (warning.Length == 0 ? "" : " ") +
                        "Windows may show a SmartScreen warning when LeGo My Cursor opens from the Start menu (Win32 " +
                        error + ").";
                }
            }
            bool modeApplied = !modePresent;
            result.Mode = mode;
            if (modePresent)
            {
                Platform.Stage("Saving your fix mode…");
                try
                {
                    Platform.WriteMode(mode); // Only this throws; the restart reports through its result.
                    Platform.Stage("Restarting your touchpad…");
                    var restart = Platform.RestartTouchpads();
                    modeApplied = RestartComplete(restart);
                    if (!modeApplied)
                        warning += " " + RestartFooter(restart);
                }
                catch (Exception e)
                {
                    int error = Platform.Code(e);
                    Platform.Log("Mode write failed: " + error + "; requested mode=" + mode);
                    warning += " The selected fix mode could not be saved (Win32 " + error +
                        "). Choose the mode again in settings.";
                }
            }
            Platform.Stage("Checking your touchpad…");
            var active = Platform.Verify(oem);
            result.Ok = true;
            if (RebootRequired)
                result.Kind = OutcomeKind.InstalledRestart;
            result.ModeActive = ModeIsActive(modeApplied, active, RebootRequired);
            result.Title = RebootRequired ? "Installed; restart required"
                : !modeApplied ? "Installed, but the selected mode needs attention"
                : active == ActiveState.Active ? "Installed and active"
                : active == ActiveState.NoDevice ? "Installed, but no Legion Go touchpad was found"
                : "Installed, but the fix isn't active";
            result.Detail = "To change the mode later, open LeGo My Cursor from the Start menu.";
            if (active == ActiveState.Inactive)
                result.Detail += " Restart Windows, then run setup again if the fix is still inactive.";
            if (skippedBefore.Count > 0)
            {
                warning += (warning.Length == 0 ? "" : " ") +
                    "Some driver store entries could not be read; see the log.";
            }
            if (warning.Length > 0)
                result.Detail += "\n\n" + warning;
            result.Warning = warning;
            Platform.Log("Install completion: mode=" + mode + " applied=" + Bit(modeApplied) +
                " active=" + Bit(result.ModeActive) + " reboot=" + Bit(RebootRequired) + "; warnings=" + warning);
            return result;
        }

        private Outcome InstallSelectedMode(uint mode)
        {
            Platform.Stage("Preparing installation…");
            Record record;
            try
            {
                record = Record.Read();
            }
            catch (Exception e)
            {
                return new Outcome(false, "The install record could not be read",
                    ErrorText("Reading the install record", e));
            }
            if (record.Present)
            {
                return new Outcome(false, "An installation already exists",
                    "Close and run setup again to change the existing installation. Nothing was removed.");
            }
            return Unrecorded() ?? Install(record, (int)mode);
        }

        private Outcome Clean()
        {
            Platform.Stage("Finding installed drivers…");
            var result = new Outcome(false, "Cleanup failed", "");
            List<string> packages, skipped;
            try
            {
                packages = Platform.FindPackages(out skipped);
            }
            catch (Exception e)
            {
                result.Detail = ErrorText("Enumerating driver packages", e);
                return result;
            }
            bool ok = true;
            foreach (string package in packages)
            {
                int error = RemovePackage(package);
                if (error != 0)
                {
                    result.Detail += ErrorText("Removing a driver package", error) + "\n";
                    ok = false;
                }
            }
            if (skipped.Count > 0)
            {
                Platform.Log("Cleanup partial: " + skipped.Count + " driver store entries could not be read");
                result.Detail += "Some driver store entries could not be read; see the log.\n";
                ok = false;
            }
            if (!ok)
            {
                // Keep the record, settings, files, and signer certificates available for retry.
                result.Title = "Cleanup was partial";
                result.Detail += "Not everything was removed. " +
                    "Run LeGoMyCursor.Setup.exe /clean again from an administrator prompt to retry.";
                return result;
            }
            Platform.Stage("Finishing cleanup…");
            string step = "Removing local signer certificates";
            try
            {
                Platform.RemoveSignerCertificates();
                step = "Removing saved settings";
                Platform.RemoveSettings(true);
                step = "Removing the install record";
                Record.Delete();
                step = "Removing the Start menu shortcut";
                Platform.RemoveShortcut();
                step = "Removing the installed application";
                Platform.RemoveInstalledFiles();
            }
            catch (Exception e)
            {
                result.Detail = ErrorText(step, e);
                return result;
            }
            return new Outcome(true, "Cleanup complete",
                "LeGo My Cursor driver packages, saved settings, local signer certificates, " +
                "and application files were removed.",
                OutcomeKind.Cleaned);
        }

        // All removals verify LeGoMyCursor identity here because oemNN.inf slots are recycled. Returns the Win32 error.
        private int RemovePackage(string oem)
        {
            int error;
            var identity = Platform.IdentifyPackage(oem, out error);
            if (identity == PackageIdentity.Error)
                return error;
            if (identity != PackageIdentity.LeGoMyCursor)
            {
                Platform.Log("recorded package " + oem + " is not LeGoMyCursor; treating as already removed");
                return 0;
            }
            bool reboot;
            Platform.Stage("Removing the driver…");
            bool ok = Platform.DiUninstallDriver(IntPtr.Zero, oem, 0, out reboot);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            RebootRequired |= reboot;
            Platform.Log("DiUninstallDriverW(" + oem + "): " + error + " reboot=" + Bit(reboot) +
                " accumulated=" + Bit(RebootRequired));
            return error;
        }

        // Removes a present record's package and certificate; returns the user-facing error, or null on success.
        private string RemoveRecorded(Record record, bool removeFiles)
        {
            int removed = RemovePackage(record.Oem);
            if (removed != 0)
                return ErrorText("Removing the recorded driver", removed);
            string step = "Removing the recorded certificate";
            try
            {
                Platform.Stage("Finishing cleanup…");
                Platform.RemoveCertificate(record.Thumbprint);
                step = "Removing saved settings";
                Platform.RemoveSettings(removeFiles);
                step = "Removing the install record";
                Record.Delete();
                if (removeFiles)
                {
                    step = "Removing the Start menu shortcut";
                    Platform.RemoveShortcut();
                    step = "Removing the installed application";
                    Platform.RemoveInstalledFiles();
                }
            }
            catch (Exception e) { return ErrorText(step, e); }
            return null;
        }

        private Outcome UninstallRecorded()
        {
            Record record;
            try
            {
                record = Record.Read();
            }
            catch (Exception e) { return RecordError(e); }
            if (!record.Present)
            {
                return new Outcome(false, "No installation was recorded",
                    "If a LeGo My Cursor driver remains installed, " +
                    "run LeGoMyCursor.Setup.exe /clean from an administrator prompt.");
            }
            string error = RemoveRecorded(record, true);
            return new Outcome(error == null, error == null ? "Uninstalled" : "Uninstall failed",
                error ?? "LeGo My Cursor was removed.", OutcomeKind.Removed);
        }
    }
}
