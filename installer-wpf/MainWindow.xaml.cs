using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LeGoMyCursor.Setup
{
    public partial class MainWindow : Window
    {
        internal UiState State { get; private set; }
        private bool closing;
        private bool sessionReboot;
        internal InstallerController Controller;

        // Status symbol paths in a 32x32 box.
        private const string RemoveSymbol = "M 5,7 L 27,7 M 10,7 L 10,3 L 22,3 L 22,7 " +
            "M 8,7 L 9,30 L 23,30 L 24,7 M 13,12 L 13,25 M 19,12 L 19,25";
        private const string RestartSymbol = "M 27,13 A 12,12 0 1 0 27,22 M 27,3 L 27,13 L 17,13";
        private const string FailureSymbol = "M 5,5 L 27,27 M 27,5 L 5,27";
        private const string WarningSymbol = "M 16,2 L 31,29 L 1,29 Z M 16,11 L 16,20 M 16,24 L 16,25";

        internal MainWindow()
        {
            InitializeComponent();
            Title = "LeGo My Cursor " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            SetLink(ReinstallButton, '', "Reinstall");
            SetLink(UninstallButton, '', "Uninstall");
            SetLink(FooterLog, '', "Open log folder");
            SourceInitialized += delegate
            {
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WindowMessage);
                RefreshTheme();
            };
            ShowPage(UiState.Loading());
        }

        private static void SetLink(Button button, char glyph, string label)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            GlyphTypeface typeface;
            var font = new FontFamily("Segoe Fluent Icons");
            if (new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
                    .TryGetGlyphTypeface(out typeface) &&
                typeface.CharacterToGlyphMap.ContainsKey(glyph))
            {
                content.Children.Add(new TextBlock
                {
                    Text = glyph.ToString(),
                    FontFamily = font,
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
            AutomationProperties.SetName(button, label);
        }

        internal void ShowPage(UiState state)
        {
            State = state;
            sessionReboot |= state.RebootRequired;
            state.RebootRequired = sessionReboot;
            bool modes = state.Kind == PageKind.Modes, progress = state.Kind == PageKind.Progress;
            bool confirm = state.Kind == PageKind.Confirmation;
            ModeScroll.Visibility = modes ? Visibility.Visible : Visibility.Collapsed;
            StatusPanel.Visibility = modes ? Visibility.Collapsed : Visibility.Visible;
            ModeHelp.Text = state.Detail;
            ModeStatus.Text = state.Status;
            if (modes)
                BuildModes();
            Heading.Text = state.Heading;
            Detail.Text = state.Detail;
            Caution.Text = state.Caution;
            Stage.Text = state.Stage;
            Caution.Visibility = string.IsNullOrEmpty(state.Caution) ? Visibility.Collapsed : Visibility.Visible;
            Stage.Visibility = progress && state.Stage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            DetailScroll.ScrollToTop();
            PrimaryButton.Content = state.Primary;
            SecondaryButton.Content = state.Secondary;
            PrimaryButton.Visibility = progress ? Visibility.Collapsed : Visibility.Visible;
            SecondaryButton.Visibility = string.IsNullOrEmpty(state.Secondary) || progress
                ? Visibility.Collapsed
                : Visibility.Visible;
            SecondaryButton.MinWidth = modes ? 96 : 116;
            PrimaryButton.IsDefault = !progress && !confirm;
            SecondaryButton.IsDefault = confirm;
            Spinner.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
            SuccessGlyph.Visibility = !modes && !confirm && !progress && state.Result == ResultKind.Success
                ? Visibility.Visible
                : Visibility.Collapsed;
            StatusGlyph.Visibility = !modes && !progress && (confirm || state.Result != ResultKind.Success)
                ? Visibility.Visible
                : Visibility.Collapsed;
            string geometry = confirm
                ? (state.Action == InstallerAction.Uninstall || state.Action == InstallerAction.Clean
                    ? RemoveSymbol
                    : RestartSymbol)
                : state.Result == ResultKind.Failure ? FailureSymbol : WarningSymbol;
            StatusPath.Data = Geometry.Parse(geometry);
            StatusPath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                confirm ? "Accent" : state.Result == ResultKind.Failure ? "Danger" : "Warning");
            var rotation = (RotateTransform)SpinnerArc.RenderTransform;
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            rotation.Angle = 0;
            if (progress)
            {
                rotation.BeginAnimation(
                    RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1400))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    });
            }
            var menu = GetSystemMenu(new WindowInteropHelper(this).Handle, false);
            if (menu != IntPtr.Zero)
                EnableMenuItem(menu, SC_CLOSE, progress ? MF_GRAYED : MF_ENABLED);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
            {
                if (State != state)
                    return;
                if (confirm)
                    SecondaryButton.Focus();
                else if (progress)
                    FooterLog.Focus();
                else if (modes)
                {
                    int row = Array.IndexOf(ModeOrder(state.Installed), state.SelectedMode);
                    ((RadioButton)ModeRows.Children[row]).Focus();
                }
                else
                    PrimaryButton.Focus();
            }));
        }

        // Recommended Simple first, then Compliant; Off is offered only for an existing installation.
        private static int[] ModeOrder(bool installed)
        {
            return installed
                ? new[] { Platform.ModeSimple, Platform.ModeCompliant, Platform.ModeOff }
                : new[] { Platform.ModeSimple, Platform.ModeCompliant };
        }

        private void BuildModes()
        {
            // Installed pages show the status line above the help text, which then needs no reserved second line.
            ModeStatus.Visibility = State.Installed ? Visibility.Visible : Visibility.Collapsed;
            ModeHelp.Margin = new Thickness(0, State.Installed ? 2 : 12, 0, 0);
            ModeHelp.MinHeight = State.Installed ? 0 : 36;
            ModeRows.Margin = new Thickness(0, State.Installed ? 10 : 24, 0, 0);
            ModeRows.Children.Clear();
            int[] order = ModeOrder(State.Installed);
            for (int row = 0; row < order.Length; ++row)
            {
                int mode = order[row], selected = mode;
                bool titleOnly = UiState.Descriptions[mode].Length == 0;
                var radio = new RadioButton
                {
                    GroupName = "Mode",
                    Style = (Style)FindResource("ModeRadio"),
                    // A title-only card is as tall as its radio ring plus the ring's top inset.
                    MinHeight = titleOnly ? 54 : mode == Platform.ModeCompliant ? 90 : 76,
                    Margin = new Thickness(0, 0, 0, !State.Installed && row != order.Length - 1 ? 12 : 0),
                    Tag = mode,
                    IsChecked = mode == State.SelectedMode
                };
                var panel = new StackPanel
                {
                    VerticalAlignment = titleOnly ? VerticalAlignment.Center : VerticalAlignment.Top
                };
                var titleRow = new DockPanel { MinHeight = 24 };
                if (State.Installed && State.SavedMode == mode)
                {
                    var badge = new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(12, 1, 12, 1),
                        Margin = new Thickness(8, 0, 0, 0),
                        Child = new TextBlock { Text = "Current" }
                    };
                    badge.SetResourceReference(Border.BorderBrushProperty, "Border");
                    DockPanel.SetDock(badge, Dock.Right);
                    titleRow.Children.Add(badge);
                }
                var title = new TextBlock
                {
                    Text = UiState.Titles[mode],
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                title.SetResourceReference(TextBlock.FontSizeProperty, "TitleSize");
                titleRow.Children.Add(title);
                panel.Children.Add(titleRow);
                var description = new TextBlock
                {
                    Text = UiState.Descriptions[mode],
                    Margin = new Thickness(0, 2, 0, 0)
                };
                if (!titleOnly)
                    panel.Children.Add(description);
                radio.Content = panel;
                Action colors = delegate
                {
                    title.SetResourceReference(TextBlock.ForegroundProperty,
                        radio.IsChecked == true ? "SelectedAccent" : "Text");
                    description.SetResourceReference(TextBlock.ForegroundProperty,
                        radio.IsChecked == true ? "SelectedMuted" : "Muted");
                };
                radio.Checked += delegate
                {
                    State.SelectedMode = selected;
                    colors();
                };
                radio.Unchecked += delegate
                {
                    colors();
                };
                colors();
                AutomationProperties.SetName(radio, (UiState.Titles[mode] + ". " + UiState.Descriptions[mode]).TrimEnd() +
                    (State.Installed && State.SavedMode == mode ? " Current mode." : ""));
                ModeRows.Children.Add(radio);
            }
            ReinstallButton.Visibility = UninstallButton.Visibility =
                State.Installed ? Visibility.Visible : Visibility.Collapsed;
            ModeLinks.Visibility = State.Installed ? Visibility.Visible : Visibility.Collapsed;
            ModeLinks.Margin = new Thickness(0, 8, 0, 0);
        }

        private void OnReinstall(object sender, RoutedEventArgs e) { Confirm("reinstall"); }

        private void OnUninstall(object sender, RoutedEventArgs e) { Confirm("uninstall"); }

        private void Confirm(string name)
        {
            Controller.Confirm(name == "reinstall" ? InstallerAction.Reinstall : InstallerAction.Uninstall);
        }

        private void OnLog(object sender, RoutedEventArgs e)
        {
            Controller.OpenLog();
        }

        private async void OnPrimary(object sender, RoutedEventArgs e)
        {
            await Controller.Primary();
        }

        private async void OnSecondary(object sender, RoutedEventArgs e)
        {
            await ActivateSecondary();
        }

        internal Task ActivateSecondary()
        {
            return Controller.Secondary();
        }

        internal void Complete(int code)
        {
            Environment.ExitCode = code;
            closing = true;
            Close();
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (closing)
                return;
            if (State.Kind == PageKind.Progress || State.Kind == PageKind.Confirmation)
            {
                e.Cancel = true; // Set before awaiting: Closing reads it when the handler returns.
                if (State.Kind == PageKind.Confirmation)
                    await ActivateSecondary();
            }
        }

        private async void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (State.Kind == PageKind.Modes && Keyboard.FocusedElement is RadioButton &&
                (e.Key == Key.Down || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Left))
            {
                int index = ModeRows.Children.IndexOf((RadioButton)Keyboard.FocusedElement);
                int direction = e.Key == Key.Down || e.Key == Key.Right ? 1 : -1;
                int count = ModeRows.Children.Count;
                var next = (RadioButton)ModeRows.Children[(index + direction + count) % count];
                next.IsChecked = true;
                next.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (State.Kind == PageKind.Progress)
                    return;
                if (State.Kind == PageKind.Confirmation)
                    await ActivateSecondary();
                else
                {
                    closing = true;
                    Close();
                }
            }
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == WM_SETTINGCHANGE || message == WM_THEMECHANGED || message == WM_SYSCOLORCHANGE)
                RefreshTheme();
            return IntPtr.Zero;
        }

        internal void RefreshTheme()
        {
            object textScale = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", 100);
            SetTextScale(textScale is int ? (int)textScale / 100.0 : 1);
            bool contrast = SystemParameters.HighContrast;
            object preference = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            bool dark = !contrast && preference is int && (int)preference == 0;
            string[] keys =
            {
                "Background", "Surface", "Selected", "Text", "Muted", "Border",
                "Accent", "Primary", "PrimaryText", "Footer", "Danger", "Warning",
                "Success", "SuccessText", "SelectedText", "SelectedMuted", "SelectedAccent"
            };
            string[] values = dark
                ? new[]
                {
                    "#1C1C1C", "#222222", "#1A3047", "#F2F2F2", "#C0C4CA", "#4B4E54",
                    "#6FB6FF", "#0067C0", "#FFFFFF", "#202020", "#FF9292", "#F3B949",
                    "#23A639", "#FFFFFF", "#F2F2F2", "#C0C4CA", "#6FB6FF"
                }
                : new[]
                {
                    "#FFFFFF", "#FFFFFF", "#EDF6FF", "#141B27", "#4A5768", "#D2D8E0",
                    "#005FB8", "#0067D2", "#FFFFFF", "#F6F7F9", "#B91C1C", "#9D6600",
                    "#23A639", "#FFFFFF", "#141B27", "#4A5768", "#005FB8"
                };
            Color[] system =
            {
                SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.HighlightColor,
                SystemColors.WindowTextColor, SystemColors.WindowTextColor, SystemColors.WindowTextColor,
                SystemColors.HighlightColor, SystemColors.HighlightColor, SystemColors.HighlightTextColor,
                SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.WindowTextColor,
                SystemColors.HighlightColor, SystemColors.HighlightTextColor, SystemColors.HighlightTextColor,
                SystemColors.HighlightTextColor, SystemColors.HighlightTextColor
            };
            for (int i = 0; i < keys.Length; ++i)
            {
                var brush = new SolidColorBrush(
                    contrast ? system[i] : (Color)ColorConverter.ConvertFromString(values[i]));
                brush.Freeze();
                Resources[keys[i]] = brush;
            }
            int enabled = dark ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref enabled, sizeof(int));
        }

        internal void SetTextScale(double scale)
        {
            scale = Math.Max(1, Math.Min(2.25, scale));
            Resources["BodySize"] = 13.333 * scale;
            Resources["HeadingSize"] = 26.667 * scale;
            Resources["TitleSize"] = 14.667 * scale;
            Resources["StageSize"] = 16.0 * scale;
            Client.Width = Math.Max(600, 500 * scale);
            Client.Height = Math.Max(450, 350 + 58 * scale);
            FooterRow.Height = new GridLength(Math.Max(58, 24 + 34 * scale));
            StatusTopRow.Height = new GridLength(Math.Max(112, 214 / scale));
            StatusSymbol.Margin = new Thickness(0, Math.Max(8, 88 - 100 * (scale - 1)), 0, 0);
        }

        private const int WM_SYSCOLORCHANGE = 0x0015;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_THEMECHANGED = 0x031A;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const uint SC_CLOSE = 0xF060;
        private const uint MF_ENABLED = 0x0;
        private const uint MF_GRAYED = 0x1;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
        [DllImport("user32.dll")]
        private static extern uint EnableMenuItem(IntPtr menu, uint item, uint enabled);
    }
}
