using System;
using System.Windows;
using System.Threading;
using System.Runtime.InteropServices;

namespace LeGoMyCursor.Setup
{
    public partial class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var app = new App();
            app.InitializeComponent();
            try
            {
                var options = InstallerOptions.Parse(args);
                options.WaitForParent(); // Wait for the parent to release the shared mutex.
                bool created;
                using (var mutex = new Mutex(true, @"Global\LeGoMyCursorInstaller", out created))
                {
                    if (!created)
                        throw new InvalidOperationException(
                            "Close the other LeGo My Cursor setup window and try again.");
                    try
                    {
                        // Attach before logging, so /probe output reaches the caller.
                        if (options.Headless)
                            AttachConsole(ATTACH_PARENT_PROCESS);
                        Platform.InitLog(Platform.SetupVersion.ToString(3),
                            Environment.CommandLine);
                        if (options.Headless)
                            Environment.ExitCode = options.Operation == SetupOperation.Probe
                                ? Lifecycle.Probe()
                                : new Lifecycle().Execute(
                                    options.Operation.Value, checked((uint)options.Mode)).ExitCode;
                        else
                        {
                            app.Resources.MergedDictionaries.Add(
                                new ResourceDictionary { Source = new Uri("Styles.xaml", UriKind.Relative) });
                            var window = new MainWindow();
                            using (var controller = new InstallerController(window))
                            {
                                window.Controller = controller;
                                app.MainWindow = window;
                                bool started = false;
                                window.ContentRendered += async delegate
                                {
                                    if (started)
                                        return;
                                    started = true;
                                    await controller.Start(options);
                                };
                                window.Closed += delegate
                                {
                                    app.Shutdown(Environment.ExitCode);
                                };
                                app.Run(window);
                            }
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                Environment.ExitCode = Platform.ERROR_INSTALL_FAILURE;
                if (Array.Exists(args, a => a.Equals("/probe", StringComparison.OrdinalIgnoreCase)) &&
                    error is System.ComponentModel.Win32Exception)
                {
                    Environment.ExitCode = ((System.ComponentModel.Win32Exception)error).NativeErrorCode;
                }
                Platform.Log("Startup/host error: " + error);
                if (Array.Exists(args, a => a.Equals("/probe", StringComparison.OrdinalIgnoreCase)))
                    Console.Error.WriteLine(error);
                else
                    MessageBox.Show(error.Message, "LeGo My Cursor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private const uint ATTACH_PARENT_PROCESS = uint.MaxValue;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(uint processId);
    }
}
