using System;
namespace LeGoMyCursor.Setup
{
    internal enum PageKind { Modes, Progress, Completion, Confirmation, Update }
    internal enum ResultKind { Success, Warning, Failure }
    internal enum InstallerAction { None, Install, Apply, Reinstall, Uninstall, Clean, Restart }

    internal sealed class UiState
    {
        internal PageKind Kind;
        internal ResultKind Result;
        internal InstallerAction Action;
        internal bool Installed, AllowChange, RebootRequired;
        internal bool Expanded; // The Update page's mode picker is open.
        internal Version InstalledVersion;
        internal int SavedMode = Platform.ModeSimple, SelectedMode = Platform.ModeSimple;
        internal string Heading = "", Detail = "", Caution = "", Stage = "", Primary = "Done", Secondary = "";
        internal string Status = ""; // The Modes page status line; the UI thread updates it when the check finishes.

        internal static UiState Progress(string heading)
        {
            return new UiState
            {
                Kind = PageKind.Progress,
                Heading = heading,
                Stage = "Preparing changes…",
                Detail = "Your touchpad may pause briefly."
            };
        }

        // Reading state changes nothing, so the page shows no stage or touchpad pause.
        internal static UiState Loading()
        {
            return new UiState { Kind = PageKind.Progress, Heading = "Loading…" };
        }

        internal static UiState Confirmation(InstallerAction action)
        {
            var state = new UiState { Kind = PageKind.Confirmation, Action = action, Secondary = "Cancel" };
            switch (action)
            {
                case InstallerAction.Reinstall:
                    state.Primary = "Reinstall";
                    state.Heading = "Reinstall LeGo My Cursor?";
                    state.Detail = "Your touchpad will pause briefly during reinstallation.";
                    break;
                case InstallerAction.Uninstall:
                    state.Primary = "Uninstall";
                    state.Heading = "Uninstall LeGo My Cursor?";
                    state.Detail = "Your touchpad will pause briefly during removal.";
                    break;
                case InstallerAction.Clean:
                    state.Primary = "Clean up";
                    state.Heading = "Clean up LeGo My Cursor?";
                    state.Detail = "This removes all copies of the touchpad fix,\n" +
                        "saved settings, and installed app files.";
                    state.Caution = "Your touchpad may pause briefly.";
                    break;
                case InstallerAction.Restart:
                    state.Primary = "Restart now";
                    state.Secondary = "Later";
                    state.Heading = "Restart now to finish?";
                    state.Detail = "Windows needs a restart to finish changing the touchpad driver.";
                    state.Caution = "Save any open work before restarting.";
                    state.RebootRequired = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("action");
            }
            return state;
        }

        // Shown instead of Modes when this setup is newer than the installed copy, so opening a new download
        // never looks like a mode change. Update reinstalls with the selected mode, which starts as the saved one;
        // the mode cards stay collapsed until the user asks to change it.
        internal static UiState Update(Version installed, int mode)
        {
            return new UiState
            {
                Kind = PageKind.Update,
                Installed = true,
                InstalledVersion = installed,
                SavedMode = mode,
                SelectedMode = mode,
                Heading = "Update to version " + Platform.SetupVersion.ToString(3) + "?",
                Detail = UpdateDetail(installed, mode, mode),
                Caution = "Your touchpad will pause briefly.",
                Primary = "Update",
                Secondary = "Cancel"
            };
        }

        internal static string UpdateDetail(Version installed, int saved, int selected)
        {
            string change = selected == Platform.ModeOff
                ? (saved == selected ? "keeps correction off." : "turns correction off.")
                : (saved == selected ? "keeps your " : "switches to ") + Titles[selected] + " mode.";
            return "Version " + installed.ToString(3) + " is installed.\n" +
                "Updating replaces the touchpad driver and " + change;
        }

        // The Modes page, or the Update page with its mode picker open.
        internal bool ShowsModes
        {
            get { return Kind == PageKind.Modes || (Kind == PageKind.Update && Expanded); }
        }

        // Indexed by mode.
        internal static readonly string[] Titles = { "Off", "Simple", "Compliant" };
        internal static readonly string[] Descriptions = {
            "",
            "Clears bad finger-lift reports. Recommended.",
            "Also fills in the finger's last position on lift, as the Precision Touchpad spec requires." };

        internal static UiState Modes(bool installed, int mode = Platform.ModeSimple)
        {
            if (mode < Platform.ModeOff || mode > Platform.ModeCompliant || (!installed && mode == Platform.ModeOff))
                throw new ArgumentOutOfRangeException("mode");
            return new UiState
            {
                Kind = PageKind.Modes,
                Installed = installed,
                SavedMode = mode,
                SelectedMode = mode,
                Status = installed ? "Status: Checking…" : "",
                Heading = "Choose your fix mode",
                Primary = installed ? "Apply mode" : "Install",
                Secondary = installed ? "Close" : "Cancel",
                Detail = installed
                    ? "Your touchpad will pause briefly while changes are applied."
                    : "Select a mode, then choose Install to add the touchpad driver. " +
                        "Your touchpad will pause briefly."
            };
        }

        internal static UiState Applied(int mode)
        {
            if (mode < Platform.ModeOff || mode > Platform.ModeCompliant)
                throw new ArgumentOutOfRangeException("mode");
            return new UiState
            {
                Kind = PageKind.Completion,
                Result = ResultKind.Success,
                Installed = true,
                SavedMode = mode,
                Heading = mode == Platform.ModeOff ? "Correction is now off" : Titles[mode] + " mode is now active",
                AllowChange = true,
                Secondary = "Change mode"
            };
        }

        internal static UiState Completion(Outcome result, SetupOperation operation)
        {
            bool modePage = operation == SetupOperation.ApplyMode || (operation == SetupOperation.Install && result.Ok);
            var state = new UiState
            {
                Kind = PageKind.Completion,
                Heading = result.Title,
                Detail = result.Detail,
                RebootRequired = result.Reboot,
                Result = result.Status
            };
            if (modePage)
            {
                state.SavedMode = (int)result.Mode;
                state.Installed = true;
                state.AllowChange = true;
                state.Secondary = "Change mode";
                // Install keeps its own title and Start menu hint, as Reinstall does.
                state.Heading = result.Kind != OutcomeKind.Mode ? result.Title
                    : result.ModeActive ? UiState.Applied((int)result.Mode).Heading
                    : "Your mode could not be fully applied";
                state.Result = result.ModeActive && result.Status == ResultKind.Success
                    ? ResultKind.Success : ResultKind.Warning;
                if (result.ModeActive && result.Kind == OutcomeKind.Mode)
                {
                    state.Detail = "";
                    state.Caution = result.Warning;
                }
            }
            if (result.Reboot && !result.ModeActive)
                state.Detail += "\n\nRestart required to finish.";
            return state;
        }
    }
}
