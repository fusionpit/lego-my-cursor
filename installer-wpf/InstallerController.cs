using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LeGoMyCursor.Setup
{
    internal sealed class InstallerController : IDisposable
    {
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly TaskCompletionSource<bool> ready =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread worker;
        private readonly MainWindow window;
        private Lifecycle lifecycle;
        private UiState returnState, pendingResult;
        private Outcome lastResult;
        private bool busy;

        internal InstallerController(MainWindow window)
        {
            this.window = window;
            worker = new Thread(Work) { Name = "LeGoMyCursor setup operations" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void Work()
        {
            int com = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            try
            {
                if (com < 0)
                    Marshal.ThrowExceptionForHR(com);
                lifecycle = new Lifecycle();
                ready.SetResult(true);
                foreach (var action in queue.GetConsumingEnumerable())
                    action();
            }
            catch (Exception error) { ready.TrySetException(error); }
            finally
            {
                if (com >= 0)
                    CoUninitialize();
            }
        }

        private async Task<T> OnWorker<T>(Func<T> action)
        {
            await ready.Task;
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add(delegate
            {
                try
                {
                    result.SetResult(action());
                }
                catch (Exception error) { result.SetException(error); }
            });
            return await result.Task;
        }

        internal async Task Start(InstallerOptions options)
        {
            try
            {
                await ready.Task;
                if (options.Operation.HasValue)
                {
                    if (options.Confirmed)
                        await Execute(options.Operation.Value);
                    else
                        Confirm(options.Operation == SetupOperation.Clean
                            ? InstallerAction.Clean
                            : InstallerAction.Uninstall);
                }
                else
                    await ReadState();
            }
            catch (Exception error) { Fail(error); }
        }

        private async Task ReadState()
        {
            busy = true;
            window.ShowPage(UiState.Loading());
            try
            {
                var result = await OnWorker(() => lifecycle.Query());
                Environment.ExitCode = result.ExitCode;
                var state = !result.Ok ? UiState.Completion(result, SetupOperation.Probe)
                    : result.Installed && result.InstalledVersion != null &&
                        result.InstalledVersion < Platform.SetupVersion
                    ? UiState.Update(result.InstalledVersion, (int)result.Mode)
                    : UiState.Modes(result.Installed, (int)result.Mode);
                window.ShowPage(state);
                if (state.Kind == PageKind.Modes && state.Installed)
                    ShowStatus(state);
            }
            finally { busy = false; }
        }

        // Verify walks every process, so it runs on the thread pool while the page stays usable.
        private void ShowStatus(UiState state)
        {
            Task.Run(() =>
            {
                string status = Lifecycle.ActiveStatus();
                window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    state.Status = status;
                    if (window.State == state)
                        window.ModeStatus.Text = status;
                }));
            });
        }

        internal void Confirm(InstallerAction action)
        {
            if (busy)
                return;
            returnState = window.State.Kind == PageKind.Modes ? window.State : null;
            window.ShowPage(UiState.Confirmation(action));
        }

        internal async Task Primary()
        {
            if (busy)
                return;
            try
            {
                var state = window.State;
                if (state.Kind == PageKind.Completion)
                {
                    window.Complete(Environment.ExitCode);
                    return;
                }
                if (state.Action == InstallerAction.Restart)
                {
                    busy = true;
                    int error = await OnWorker(() => Platform.RequestRestart());
                    if (error == 0)
                        window.Complete(Environment.ExitCode);
                    else
                        window.ShowPage(new UiState
                        {
                            Kind = PageKind.Completion,
                            Result = ResultKind.Warning,
                            Heading = "Windows could not restart",
                            RebootRequired = true,
                            Detail = "Restart Windows manually to finish.\nWindows error " + error + ": " +
                                new Win32Exception(error).Message + "\n\n" + Lifecycle.RestartContext(lastResult)
                        });
                    return;
                }
                SetupOperation operation = state.Kind == PageKind.Update ? SetupOperation.Update
                    : state.Kind == PageKind.Modes
                    ? (state.Installed ? SetupOperation.ApplyMode : SetupOperation.Install)
                    : state.Action == InstallerAction.Reinstall ? SetupOperation.Reinstall
                    : state.Action == InstallerAction.Clean ? SetupOperation.Clean
                    : SetupOperation.Uninstall;
                await Execute(operation, state.SelectedMode);
            }
            catch (Exception error) { Fail(error); }
            finally { busy = false; }
        }

        // Uninstall and Clean ignore mode.
        private async Task Execute(SetupOperation operation, int mode = 0)
        {
            busy = true;
            var progress = UiState.Progress(
                operation == SetupOperation.Uninstall ? "Uninstalling LeGo My Cursor…"
                : operation == SetupOperation.Clean ? "Cleaning LeGo My Cursor…"
                : operation == SetupOperation.Reinstall ? "Reinstalling LeGo My Cursor…"
                : operation == SetupOperation.Update ? "Updating to version " + Platform.SetupVersion.ToString(3) + "…"
                : operation == SetupOperation.Install ? "Installing LeGo My Cursor…"
                : mode == Platform.ModeOff ? "Turning correction off"
                : "Applying " + UiState.Titles[mode] + " mode");
            window.ShowPage(progress);
            try
            {
                if (operation == SetupOperation.Uninstall || operation == SetupOperation.Clean)
                {
                    if (await OnWorker(() => Platform.Handoff(
                        operation == SetupOperation.Uninstall ? "/uninstall" : "/clean")))
                    {
                        window.Complete(0);
                        return;
                    }
                }
                lastResult = await OnWorker(() =>
                {
                    Platform.StageSink = text => window.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (window.State == progress)
                            window.Stage.Text = text;
                    }));
                    try
                    {
                        return lifecycle.Execute(operation, checked((uint)mode));
                    }
                    finally { Platform.StageSink = null; }
                });
                Environment.ExitCode = operation == SetupOperation.ApplyMode ? 0 : lastResult.ExitCode;
                pendingResult = UiState.Completion(lastResult, operation);
                if (lastResult.Reboot && operation != SetupOperation.ApplyMode)
                    window.ShowPage(UiState.Confirmation(InstallerAction.Restart));
                else
                    window.ShowPage(pendingResult);
            }
            finally { busy = false; }
        }

        internal async Task Secondary()
        {
            if (busy)
                return;
            try
            {
                if (window.State.Kind == PageKind.Confirmation)
                {
                    if (window.State.Action == InstallerAction.Restart)
                        window.ShowPage(pendingResult);
                    else if (returnState != null)
                        window.ShowPage(returnState);
                    else
                        window.Complete(0);
                }
                else if (window.State.AllowChange)
                    await ReadState();
                else
                    window.Complete(Environment.ExitCode);
            }
            catch (Exception error) { Fail(error); }
        }

        internal void OpenLog()
        {
            try
            {
                Platform.OpenLogFolder();
            }
            catch (Exception error)
            {
                System.Windows.MessageBox.Show(window, error.Message, "Could not open log folder");
            }
        }

        private void Fail(Exception error)
        {
            busy = false;
            Environment.ExitCode = Platform.ERROR_INSTALL_FAILURE;
            window.ShowPage(new UiState
            {
                Kind = PageKind.Completion,
                Result = ResultKind.Failure,
                Heading = "Setup could not finish",
                Detail = error.Message + "\n\nOpen the log folder for details. Run setup again to retry."
            });
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            worker.Join();
            queue.Dispose();
        }

        private const uint COINIT_APARTMENTTHREADED = 0x2;

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
    }
}
