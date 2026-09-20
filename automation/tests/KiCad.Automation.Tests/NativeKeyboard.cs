using System.Runtime.InteropServices;
using System.Diagnostics;

namespace KiCad.Automation.Tests;

/// <summary>Rendered-interface input on the fixture-owned X server only. Does
/// not search the user's display or invoke a native editor action directly.</summary>
internal static class NativeKeyboard
{
    public static async Task CaptureAsync(string fixtureDisplay, string destination, CancellationToken token)
    {
        var start = new ProcessStartInfo("ffmpeg")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-nostdin", "-y", "-loglevel", "error", "-f", "x11grab", "-video_size", "1280x900",
            "-i", fixtureDisplay, "-frames:v", "1", destination }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(output, error);
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Fixture screenshot failed: " + await error);
    }

    public static bool HasWindow(string fixtureDisplay, int processId, string titleMatch, Action<string>? describe = null,
        nuint? excludeWindow = null)
    {
        try
        {
            SchematicShortcut(fixtureDisplay, processId, "", titleMatch, false, false, describe: describe, excludeWindow: excludeWindow);
            return true;
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith($"Expected one '{titleMatch}'", StringComparison.Ordinal))
        {
            return false;
        }
    }

    // focusCanvas controls the optional pre-click for keyboard input. Explicit
    // pointer commands still have to send their requested physical action.
    internal static bool RequestsPointerInput(string key, bool focusCanvas) => key.Length != 0
        && (focusCanvas || key is "click" or "right-click" or "motion" or "drag");

    public static void SchematicShortcut(string fixtureDisplay, int processId, string key,
        string titleMatch = "Schematic Editor", bool controlKey = true, bool focusCanvas = true,
        int? clickFromRight = null, int? clickFromBottom = null, Action<string>? describe = null,
        int? clickFromLeft = null, int? clickFromTop = null, bool altKey = false,
        Action<nuint>? observeWindow = null, Action<(int X, int Y, int Width, int Height)>? observeGeometry = null,
        nuint? excludeWindow = null, Action<int>? observePopupCount = null,
        int? dragToLeft = null, int? dragToTop = null, int? resizeWidth = null, int? resizeHeight = null)
    {
        ValidateDragArguments(key, dragToLeft, dragToTop);
        if (resizeWidth.HasValue != resizeHeight.HasValue || resizeWidth is <= 0 || resizeHeight is <= 0
            || resizeWidth.HasValue && key.Length != 0)
            throw new ArgumentException("Window resizing requires a separate exact-target operation and two positive dimensions.");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var errors = new WindowErrorScope();
        nint display = XOpenDisplay(fixtureDisplay);
        if (display == 0) throw new InvalidOperationException("Fixture display is unavailable.");
        try
        {
            nuint pidAtom = XInternAtom(display, "_NET_WM_PID", 1);
            nuint titleAtom = XInternAtom(display, "_NET_WM_NAME", 1);
            if (pidAtom == 0) throw new InvalidOperationException("The fixture editor has no process identity property.");
            nuint root = XDefaultRootWindow(display);
            if (XQueryTree(display, root, out _, out _, out nint children, out uint count) == 0)
                throw new InvalidOperationException("Cannot inspect fixture windows.");
            var targets = new List<nuint>();
            var observed = new List<string>();
            int popupCount = 0;
            try
            {
                for (int index = 0; index < count; index++)
                {
                    nuint window = (nuint)Marshal.ReadIntPtr(children, index * nint.Size);
                    nint value = 0, title = 0;
                    try
                    {
                        int status = XGetWindowProperty(display, window, pidAtom, 0, 1, 0, 0,
                            out _, out int format, out nuint items, out _, out value);
                        string name = "";
                        if (titleAtom != 0 && XGetWindowProperty(display, window, titleAtom, 0, 4096, 0, 0,
                            out _, out int titleFormat, out nuint titleLength, out _, out title) == 0
                            && titleFormat == 8 && title != 0)
                            name = Marshal.PtrToStringUTF8(title, checked((int)titleLength)) ?? "";
                        if (name.Length == 0)
                        {
                            if (title != 0) XFree(title);
                            title = 0;
                            if (XFetchName(display, window, out title) != 0 && title != 0)
                                name = Marshal.PtrToStringUTF8(title) ?? "";
                        }
                        nint owner = status == 0 && format == 32 && items == 1 && value != 0
                            ? Marshal.ReadIntPtr(value) : 0;
                        if (observed.Count < 12) observed.Add($"pid={owner}, title={name}");
                        if (owner != processId) continue;
                        // GTK may retain a hidden native window after a dialog
                        // closes. Its title is not evidence of a visible UI.
                        if (XGetWindowAttributes(display, window, out var attributes) == 0) continue;
                        describe?.Invoke($"Fixture window {window}: {name}, map={attributes.MapState}, "
                            + $"geometry={attributes.X},{attributes.Y},{attributes.Width},{attributes.Height}");
                        if (attributes.MapState != 2) continue; // X11 IsViewable.
                        // GTK context menus are viewable override-redirect
                        // windows. Exclude its tiny offscreen grab windows.
                        if (IsVisiblePopup(attributes.MapState, attributes.OverrideRedirect, attributes.Width, attributes.Height))
                            popupCount++;
                        if (window != excludeWindow && name.Contains(titleMatch, StringComparison.Ordinal)) targets.Add(window);
                    }
                    finally
                    {
                        if (value != 0) XFree(value);
                        if (title != 0) XFree(title);
                    }
                }
            }
            finally { if (children != 0) XFree(children); }
            XSync(display, 0);
            errors.Enumerating = false;
            observePopupCount?.Invoke(popupCount);
            if (targets.Count != 1)
                throw new InvalidOperationException($"Expected one '{titleMatch}' window for fixture process {processId}; found {targets.Count}. "
                    + string.Join("; ", observed));

            observeWindow?.Invoke(targets.Single());
            if (resizeWidth is int requestedWidth && resizeHeight is int requestedHeight)
            {
                if (XResizeWindow(display, targets[0], (uint)requestedWidth, (uint)requestedHeight) == 0)
                    throw new InvalidOperationException("The fixture window could not be resized.");
                XSync(display, 0);
            }
            if (observeGeometry is not null)
            {
                if (XGetWindowAttributes(display, targets[0], out var geometry) == 0)
                    throw new InvalidOperationException("Cannot inspect fixture window geometry.");
                observeGeometry((geometry.X, geometry.Y, geometry.Width, geometry.Height));
            }
            if (key.Length == 0) return; // Read-only fixture window-presence query.

            XRaiseWindow(display, targets[0]);
            XSetInputFocus(display, targets[0], 2, 0); // RevertToParent, CurrentTime.
            if (XGetGeometry(display, targets[0], out _, out _, out _, out uint width, out uint height, out _, out _) == 0
                || XTranslateCoordinates(display, targets[0], root,
                    clickFromLeft ?? (clickFromRight is int right ? (int)width - right : (int)width - 100),
                    clickFromTop ?? (clickFromBottom is int bottom ? (int)height - bottom : (int)height - 100),
                    out int pointerX, out int pointerY, out _) == 0)
                throw new InvalidOperationException("Cannot locate the fixture's rendered canvas.");
            // Give the canvas widget keyboard focus through a real background
            // click. X input focus alone does not select a GTK focus widget.
            // The fixture's lower-right margin is outside the drawing sheet;
            // its former 3/4-width point hit the page border after fit/reload,
            // where repeated clicks legitimately open Page Settings.
            if (RequestsPointerInput(key, focusCanvas))
            {
                if (key == "drag")
                {
                    if (dragToLeft is not int endX || dragToTop is not int endY
                        || XTranslateCoordinates(display, targets[0], root, endX, endY, out int finishX, out int finishY, out _) == 0)
                        throw new InvalidOperationException("A fixture drag needs an explicit endpoint.");
                    XTestFakeMotionEvent(display, -1, pointerX, pointerY, 0);
                    try
                    {
                        if (XTestFakeButtonEvent(display, 1, 1, 0) == 0) throw new InvalidOperationException("Fixture drag press failed.");
                        for (int step = 1; step <= 8; step++)
                            if (XTestFakeMotionEvent(display, -1, pointerX + (finishX - pointerX) * step / 8,
                                pointerY + (finishY - pointerY) * step / 8, 20) == 0)
                                throw new InvalidOperationException("Fixture drag motion failed.");
                    }
                    finally { XTestFakeButtonEvent(display, 1, 0, 20); XSync(display, 0); }
                    return;
                }
                XTestFakeMotionEvent(display, -1, pointerX, pointerY, 0);
                if (key != "motion")
                {
                    uint button = key == "right-click" ? 3U : 1U;
                    XTestFakeButtonEvent(display, button, 1, 0);
                    XTestFakeButtonEvent(display, button, 0, 0);
                }
            }
            XSync(display, 0);
            if (key is "click" or "right-click" or "motion") return;
            byte control = XKeysymToKeycode(display, XStringToKeysym(altKey ? "Alt_L" : "Control_L"));
            nuint requested = LiteralKeysym(key) ?? XStringToKeysym(key);
            byte character = XKeysymToKeycode(display, requested);
            if (control == 0 || character == 0) throw new InvalidOperationException("Fixture keymap lacks the requested shortcut.");
            bool shifted = RequiresShift(requested, XkbKeycodeToKeysym(display, character, 0, 0),
                XkbKeycodeToKeysym(display, character, 0, 1));
            byte shift = shifted ? XKeysymToKeycode(display, XStringToKeysym("Shift_L")) : (byte)0;
            if (shifted && shift == 0) throw new InvalidOperationException("Fixture keymap lacks Shift.");
            try
            {
                if (((controlKey || altKey) && XTestFakeKeyEvent(display, control, 1, 0) == 0)
                    || (shifted && XTestFakeKeyEvent(display, shift, 1, 0) == 0)
                    || XTestFakeKeyEvent(display, character, 1, 0) == 0)
                    throw new InvalidOperationException("XTest rejected fixture keyboard input.");
            }
            finally
            {
                XTestFakeKeyEvent(display, character, 0, 0);
                if (shifted) XTestFakeKeyEvent(display, shift, 0, 0);
                if (controlKey || altKey) XTestFakeKeyEvent(display, control, 0, 0);
                XSync(display, 0);
            }
        }
        finally { XCloseDisplay(display); }
    }

    internal static void ValidateDragArguments(string key, int? x, int? y)
    {
        if (key == "drag" ? x is null || y is null : x is not null || y is not null)
            throw new ArgumentException("Only a drag command accepts endpoint coordinates, and both coordinates are required.");
    }

    // Printable Latin-1 keysyms equal their character values. Xlib names such
    // as "underscore" are not interchangeable with the literal "_" string.
    internal static nuint? LiteralKeysym(string key) =>
        key.Length == 1 && key[0] is >= ' ' and <= '\u00ff' ? key[0] : null;

    internal static bool RequiresShift(nuint requested, nuint plain, nuint shifted)
    {
        if (requested != 0 && requested == plain) return false;
        if (requested != 0 && requested == shifted) return true;
        throw new InvalidOperationException("Requested fixture key is not available in the unshifted or shifted keymap.");
    }

    internal static bool IsWindowEnumerationRace(byte error, byte request) =>
        error == 3 && request is 3 or 14 or 15 or 20; // BadWindow on read-only window inspection.

    internal static bool IsVisiblePopup(int mapState, int overrideRedirect, int width, int height) =>
        mapState == 2 && overrideRedirect != 0 && width > 20 && height > 20;

    private sealed class WindowErrorScope : IDisposable
    {
        private static readonly object Gate = new();
        private readonly ErrorHandler handler;
        private readonly nint previous;
        private string? failure;
        public bool Enumerating { get; set; } = true;

        public WindowErrorScope()
        {
            Monitor.Enter(Gate);
            handler = Handle;
            try { previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(handler)); }
            catch { Monitor.Exit(Gate); throw; }
        }

        private int Handle(nint display, nint error)
        {
            var value = Marshal.PtrToStructure<WindowError>(error);
            if (!Enumerating || !IsWindowEnumerationRace(value.ErrorCode, value.RequestCode))
                failure = $"X11 fixture error {value.ErrorCode}, request {value.RequestCode}, resource {value.Resource}";
            return 0;
        }

        public void Dispose()
        {
            try { XSetErrorHandler(previous); GC.KeepAlive(handler); }
            finally { Monitor.Exit(Gate); }
            if (failure is not null) throw new InvalidOperationException(failure);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ErrorHandler(nint display, nint error);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowError
    {
        public int Type;
        public nint Display;
        public nuint Resource, Serial;
        public byte ErrorCode, RequestCode, MinorCode;
    }

    [DllImport("libX11.so.6")] private static extern nint XSetErrorHandler(nint handler);

    // Mirrors XWindowAttributes in the platform X11/Xlib.h. Xlib longs and
    // pointers use native width; Bool and enum fields use C int width.
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public nint Visual;
        public nuint Root;
        public int Class, BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled, MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    [DllImport("libX11.so.6")] private static extern int XGetWindowAttributes(nint display,
        nuint window, out WindowAttributes attributes);
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(string display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XInternAtom(nint display, string name, int onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XQueryTree(nint display, nuint window,
        out nuint root, out nuint parent, out nint children, out uint count);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(nint display, nuint window,
        nuint property, nint offset, nint length, int delete, nuint requestedType,
        out nuint actualType, out int format, out nuint count, out nuint bytesAfter, out nint value);
    [DllImport("libX11.so.6")] private static extern int XFetchName(nint display, nuint window, out nint name);
    [DllImport("libX11.so.6")] private static extern int XFree(nint memory);
    [DllImport("libX11.so.6")] private static extern int XRaiseWindow(nint display, nuint window);
    [DllImport("libX11.so.6")] private static extern int XResizeWindow(nint display, nuint window, uint width, uint height);
    [DllImport("libX11.so.6")] private static extern int XGetGeometry(nint display, nuint window,
        out nuint root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
    [DllImport("libX11.so.6")] private static extern int XTranslateCoordinates(nint display, nuint source, nuint destination,
        int x, int y, out int translatedX, out int translatedY, out nuint child);
    [DllImport("libX11.so.6")] private static extern int XSetInputFocus(nint display, nuint window, int revertTo, nuint time);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, int discard);
    [DllImport("libX11.so.6")] private static extern nuint XStringToKeysym(string name);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(nint display, nuint keysym);
    [DllImport("libX11.so.6")] private static extern nuint XkbKeycodeToKeysym(nint display, byte keycode, int group, int level);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(nint display, uint keycode, int isPress, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeButtonEvent(nint display, uint button, int isPress, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
}
