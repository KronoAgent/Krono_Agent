using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Windows.System;

namespace Krono_Agent;

public sealed partial class MainWindow
{
    const uint AppCommandMessage = 0x0319;
    const uint XButtonDown = 0x020B, XButtonUp = 0x020C;
    const nuint NavigationSubclass = 2;
    NavigationWindowProc? navigationWindowProc;
    MessageHook? navigationMouseHook;

    void InitializeAppNavigation()
    {
        AppBack.Content = BrowserWorkspace.ToolbarIcon(Symbol.Back);
        AppForward.Content = BrowserWorkspace.ToolbarIcon(Symbol.Forward);
        KronoInputMotion.StyleButton(AppBack);
        KronoInputMotion.StyleButton(AppForward);
        InitializeNavigationHitRegion();
        InitializeNavigationHints();
        AppBack.Click += (_, _) => NavigateHistory(-1);
        AppForward.Click += (_, _) => NavigateHistory(1);
        if (Content is UIElement root) {
            AddShortcut(root, VirtualKey.Left, -1);
            AddShortcut(root, VirtualKey.Right, 1);
        }
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        navigationWindowProc = HandleNavigationCommand;
        if (SetWindowSubclass(window, navigationWindowProc, NavigationSubclass, 0))
            Closed += (_, _) => RemoveWindowSubclass(window, navigationWindowProc, NavigationSubclass);
        uint capturedSideButtons = 0;
        navigationMouseHook = (code, message, address) => {
            if (code >= 0 && message is XButtonDown or XButtonUp && GetForegroundWindow() == window) {
                var click = Marshal.PtrToStructure<MouseHookData>(address);
                var button = click.MouseData >> 16;
                if (button is 1 or 2) {
                    var direction = button == 1 ? -1 : 1;
                    var action = direction < 0 ? AppBack : AppForward;
                    if (message == XButtonDown && action.IsEnabled) {
                        capturedSideButtons |= button;
                        return 1;
                    }
                    // Consume both halves of the click to keep WinUI's pointer state balanced.
                    if (message == XButtonUp && (capturedSideButtons & button) != 0) {
                        capturedSideButtons &= ~button;
                        QueueNavigation(direction);
                        return 1;
                    }
                }
            }
            return CallNextHookEx(0, code, message, address);
        };
        // WinUI may consume extended mouse messages before forwarding an app command.
        // Handle only side buttons while Krono is the foreground window.
        nint hook = 0;
        void RemoveMouseHook()
        {
            if (hook == 0) return;
            UnhookWindowsHookEx(hook);
            hook = 0;
            capturedSideButtons = 0;
        }
        void InstallMouseHook()
        {
            if (hook != 0) return;
            hook = SetWindowsHookExW(14, navigationMouseHook, GetModuleHandleW(null), 0);
            if (hook == 0) App.RecordFault("Mouse navigation hook", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (GetForegroundWindow() == window) InstallMouseHook();
        Activated += (_, args) => {
            if (args.WindowActivationState == WindowActivationState.Deactivated) RemoveMouseHook();
            else InstallMouseHook();
        };
        Closed += (_, _) => RemoveMouseHook();
    }

    void InitializeNavigationHitRegion()
    {
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        Windows.Graphics.RectInt32? last = null;
        NavigationButtons.Loaded += (_, _) => {
            if (NavigationButtons.XamlRoot is { } root) root.Changed += (_, _) => UpdateRegion();
            UpdateRegion();
        };
        NavigationButtons.SizeChanged += (_, _) => UpdateRegion();
        AppTitleBar.SizeChanged += (_, _) => UpdateRegion();

        void UpdateRegion()
        {
            if (NavigationButtons.XamlRoot is not { } root || NavigationButtons.ActualWidth <= 0) return;
            var bounds = NavigationButtons.TransformToVisual(WindowLayout).TransformBounds(
                new Windows.Foundation.Rect(0, 0, NavigationButtons.ActualWidth, NavigationButtons.ActualHeight));
            var scale = root.RasterizationScale;
            var rect = new Windows.Graphics.RectInt32((int)Math.Floor(bounds.Left * scale), (int)Math.Floor(bounds.Top * scale),
                (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale));
            if (rect.Equals(last)) return;
            source.SetRegionRects(NonClientRegionKind.Passthrough, [rect]);
            last = rect;
        }
    }

    void InitializeNavigationHints()
    {
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        var back = FrameworkElementAutomationPeer.CreatePeerForElement(AppBack);
        var forward = FrameworkElementAutomationPeer.CreatePeerForElement(AppForward);
        FrameworkElement? hovered = null;
        long enteredAt = 0;

        timer.Tick += (_, _) => {
            if (GetForegroundWindow() != window || !GetCursorPos(out var cursor)) {
                HideHint();
                return;
            }

            // Use screen coordinates because Windows can deliver caption-area pointer
            // movement through the non-client path even beside interactive controls.
            var point = new Windows.Foundation.Point(cursor.X, cursor.Y);
            var target = back?.GetBoundingRectangle().Contains(point) == true ? BackHintTarget :
                forward?.GetBoundingRectangle().Contains(point) == true ? ForwardHintTarget : null;
            var now = Environment.TickCount64;
            if (target is null) { HideHint(); return; }
            if (!ReferenceEquals(hovered, target)) {
                hovered = target;
                enteredAt = NavigationHint.Visibility == Visibility.Visible ? now - 400 : now;
            }
            if (now - enteredAt < 400) return;

            var label = target == BackHintTarget ? "Go back (Alt+Left)" : "Go forward (Alt+Right)";
            if (NavigationHint.Visibility == Visibility.Visible && NavigationHintText.Text == label) return;
            var origin = target.TransformToVisual(WindowLayout).TransformPoint(new(0, target.ActualHeight));
            NavigationHint.Margin = new Thickness(origin.X, origin.Y + 4, 0, 0);
            NavigationHintText.Text = label;
            AutomationProperties.SetName(NavigationHintText, label);
            NavigationHint.Visibility = Visibility.Visible;
        };
        Activated += (_, args) => {
            if (args.WindowActivationState == WindowActivationState.Deactivated) Stop();
            else timer.Start();
        };
        if (GetForegroundWindow() == window) timer.Start();
        Closed += (_, _) => Stop();

        void HideHint()
        {
            hovered = null;
            NavigationHint.Visibility = Visibility.Collapsed;
        }

        void Stop()
        {
            timer.Stop();
            HideHint();
        }
    }

    void AddShortcut(UIElement root, VirtualKey key, int direction)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Menu };
        accelerator.Invoked += (_, args) => args.Handled = NavigateHistory(direction);
        root.KeyboardAccelerators.Add(accelerator);
    }

    internal void UpdateNavigationButtons(bool back, bool forward)
    {
        AppBack.IsEnabled = back;
        AppForward.IsEnabled = forward;
    }

    bool NavigateHistory(int direction) =>
        RootFrame.Content is MainPage page && page.NavigateHistory(direction);

    bool QueueNavigation(int direction) =>
        (direction < 0 ? AppBack : AppForward).IsEnabled &&
        DispatcherQueue.TryEnqueue(() => NavigateHistory(direction));

    nint HandleNavigationCommand(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        var command = ((uint)lParam >> 16) & 0x0FFF;
        if (message == AppCommandMessage && command is 1 or 2) {
            var direction = command == 1 ? -1 : 1;
            if (QueueNavigation(direction)) return 1;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    delegate nint NavigationWindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(nint window, NavigationWindowProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(nint window, NavigationWindowProc callback, nuint id);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseHookData
    {
        public NativePoint Position;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    delegate nint MessageHook(int code, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookExW(int id, MessageHook callback, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern nint GetModuleHandleW(string? module);
}
