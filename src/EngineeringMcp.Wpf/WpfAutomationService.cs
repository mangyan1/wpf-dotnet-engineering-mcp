using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace EngineeringMcp.Wpf;

public sealed class WpfAutomationService(
    ProcessGuard processGuard,
    FilePolicyProvider policyProvider,
    RedactionService redactionService)
{
    private sealed class AttachedSession : IDisposable
    {
        public required int ProcessId { get; init; }
        public required Application Application { get; init; }
        public required UIA3Automation Automation { get; init; }
        public ConcurrentDictionary<string, AutomationElement> References { get; } = new(StringComparer.Ordinal);
        public long NextReference;

        public string Register(AutomationElement element)
        {
            var id = $"uia:{ProcessId}:{Interlocked.Increment(ref NextReference)}";
            References[id] = element;
            return id;
        }

        public void Dispose()
        {
            References.Clear();
            Automation.Dispose();
            Application.Dispose();
        }
    }

    private readonly ConcurrentDictionary<int, AttachedSession> _sessions = new();

    public IReadOnlyList<ProcessDescriptor> ListAllowedProcesses()
    {
        var results = new List<ProcessDescriptor>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                ProcessDescriptor descriptor;
                try { descriptor = processGuard.Describe(process); }
                catch { continue; }
                if (descriptor.Allowed) results.Add(descriptor);
            }
        }
        return results.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProcessId).ToArray();
    }

    public ToolResult<object> Attach(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return ToolResult<object>.Fail("WINDOWS_REQUIRED", "WPF/UIA automation is available only on Windows.");

        if (_sessions.ContainsKey(processId))
            return ToolResult<object>.Ok(new { processId, attached = true, alreadyAttached = true });

        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success || allowed.Value is null)
            return ToolResult<object>.Fail(
                allowed.Error?.Code ?? "PROCESS_NOT_ALLOWED",
                allowed.Error?.Message ?? "Process is not allowed.",
                allowed.Error?.Retryable ?? false,
                allowed.Error?.Remediation);

        using var process = allowed.Value;
        try
        {
            var app = Application.Attach(processId);
            var automation = new UIA3Automation();
            var session = new AttachedSession { ProcessId = processId, Application = app, Automation = automation };
            if (!_sessions.TryAdd(processId, session))
            {
                session.Dispose();
                return ToolResult<object>.Ok(new { processId, attached = true, alreadyAttached = true });
            }
            return ToolResult<object>.Ok(new { processId, attached = true });
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail("WPF_ATTACH_FAILED", redactionService.Redact(ex.Message, policyProvider.Current.Pii));
        }
    }

    public ToolResult<IReadOnlyList<WindowDescriptor>> ListWindows(int processId)
    {
        var sessionResult = RequireSession(processId);
        if (!sessionResult.Success || sessionResult.Value is null)
            return ToolResult<IReadOnlyList<WindowDescriptor>>.Fail(sessionResult.Error!.Code, sessionResult.Error.Message);

        try
        {
            var session = sessionResult.Value;
            var windows = session.Application.GetAllTopLevelWindows(session.Automation);
            var output = windows.Select(window =>
            {
                var reference = session.Register(window);
                return new WindowDescriptor(
                    reference,
                    SafeText(window.Properties.Name.ValueOrDefault),
                    processId,
                    ToRect(window.Properties.BoundingRectangle.ValueOrDefault),
                    window.Properties.IsEnabled.ValueOrDefault,
                    window.Properties.IsOffscreen.ValueOrDefault);
            }).ToArray();
            return ToolResult<IReadOnlyList<WindowDescriptor>>.Ok(output);
        }
        catch (Exception ex)
        {
            return ToolResult<IReadOnlyList<WindowDescriptor>>.Fail("WPF_WINDOW_ENUMERATION_FAILED", SafeException(ex));
        }
    }

    public ToolResult<UiSnapshot> Snapshot(int processId, string? windowReference = null, int maxElements = 500, int maxDepth = 12)
    {
        maxElements = Math.Clamp(maxElements, 1, 5_000);
        maxDepth = Math.Clamp(maxDepth, 1, 64);

        var sessionResult = RequireSession(processId);
        if (!sessionResult.Success || sessionResult.Value is null)
            return ToolResult<UiSnapshot>.Fail(sessionResult.Error!.Code, sessionResult.Error.Message);

        try
        {
            var session = sessionResult.Value;
            AutomationElement? rootElement = null;
            string rootRef;
            if (!string.IsNullOrWhiteSpace(windowReference))
            {
                if (!session.References.TryGetValue(windowReference, out rootElement))
                    return ToolResult<UiSnapshot>.Fail("ELEMENT_REFERENCE_NOT_FOUND", "Window reference is unknown or stale.");
                rootRef = windowReference;
            }
            else
            {
                rootElement = session.Application.GetMainWindow(session.Automation)
                    ?? session.Application.GetAllTopLevelWindows(session.Automation).FirstOrDefault();
                if (rootElement is null)
                    return ToolResult<UiSnapshot>.Fail("WPF_WINDOW_NOT_FOUND", "No top-level WPF window was found.");
                rootRef = session.Register(rootElement);
            }

            var elements = new List<UiElementSnapshot>(Math.Min(maxElements, 1024));
            var queue = new Queue<(AutomationElement Element, string Ref, string? ParentRef, int Depth)>();
            queue.Enqueue((rootElement, rootRef, null, 0));
            var truncated = false;

            while (queue.Count > 0)
            {
                if (elements.Count >= maxElements) { truncated = true; break; }
                var (element, reference, parentRef, depth) = queue.Dequeue();
                elements.Add(ToSnapshot(element, reference, parentRef, depth));

                if (depth >= maxDepth) continue;
                AutomationElement[] children;
                try { children = element.FindAllChildren(); }
                catch { continue; }

                foreach (var child in children)
                {
                    if (elements.Count + queue.Count >= maxElements)
                    {
                        truncated = true;
                        break;
                    }
                    queue.Enqueue((child, session.Register(child), reference, depth + 1));
                }
            }

            return ToolResult<UiSnapshot>.Ok(new UiSnapshot(processId, rootRef, DateTimeOffset.UtcNow, elements, truncated, maxElements));
        }
        catch (Exception ex)
        {
            return ToolResult<UiSnapshot>.Fail("WPF_SNAPSHOT_FAILED", SafeException(ex));
        }
    }

    public ToolResult<UiElementSnapshot> Find(int processId, UiSelector selector) => ResolveSnapshot(processId, selector);
    public ToolResult<UiElementSnapshot> Query(int processId, UiSelector selector) => ResolveSnapshot(processId, selector);

    public ToolResult<UiElementSnapshot> Wait(int processId, UiSelector selector, int timeoutMs = 5_000, bool requireEnabled = false, bool requireVisible = false, CancellationToken cancellationToken = default)
    {
        timeoutMs = Math.Clamp(timeoutMs, 50, 60_000);
        var deadline = Environment.TickCount64 + timeoutMs;
        ToolFailure? lastFailure = null;
        while (Environment.TickCount64 <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Query(processId, selector);
            if (current.Success && current.Value is not null)
            {
                var stateSatisfied = (!requireEnabled || current.Value.IsEnabled) && (!requireVisible || !current.Value.IsOffscreen);
                if (stateSatisfied) return current;
                lastFailure = new ToolFailure("WAIT_STATE_NOT_READY", "Element exists but has not reached the requested state yet.", true);
            }
            else
            {
                lastFailure = current.Error;
            }
            if (cancellationToken.WaitHandle.WaitOne(100)) cancellationToken.ThrowIfCancellationRequested();
        }

        return ToolResult<UiElementSnapshot>.Fail(
            "WAIT_TIMEOUT",
            lastFailure is null ? "Timed out waiting for the UI element." : $"Timed out waiting for the UI element. Last observation: {lastFailure.Code}.",
            false);
    }

    public ToolResult<UiAssertionResult> Assert(int processId, UiSelector selector, bool? enabled = null, bool? offscreen = null, bool? keyboardFocusable = null, string? expectedName = null)
    {
        var current = Query(processId, selector);
        if (!current.Success || current.Value is null)
            return ToolResult<UiAssertionResult>.Fail(current.Error!.Code, current.Error.Message, current.Error.Retryable);

        var failures = new List<string>();
        if (enabled is not null && current.Value.IsEnabled != enabled.Value)
            failures.Add($"IsEnabled expected {enabled.Value} but was {current.Value.IsEnabled}.");
        if (offscreen is not null && current.Value.IsOffscreen != offscreen.Value)
            failures.Add($"IsOffscreen expected {offscreen.Value} but was {current.Value.IsOffscreen}.");
        if (keyboardFocusable is not null && current.Value.IsKeyboardFocusable != keyboardFocusable.Value)
            failures.Add($"IsKeyboardFocusable expected {keyboardFocusable.Value} but was {current.Value.IsKeyboardFocusable}.");
        if (expectedName is not null && !string.Equals(current.Value.Name, SafeText(expectedName), StringComparison.Ordinal))
            failures.Add("Name did not match the expected redacted value.");

        return ToolResult<UiAssertionResult>.Ok(new UiAssertionResult(failures.Count == 0, current.Value, failures));
    }

    public ToolResult<object> Click(int processId, UiSelector selector)
    {
        var elementResult = ResolveElement(processId, selector);
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<object>.Fail(elementResult.Error!.Code, elementResult.Error.Message);

        var element = elementResult.Value;
        if (!element.Properties.IsEnabled.ValueOrDefault)
            return ToolResult<object>.Fail("ELEMENT_DISABLED", "Target element is disabled.");
        if (element.Properties.IsPassword.ValueOrDefault)
            return ToolResult<object>.Fail("SENSITIVE_CONTROL_DENIED", "Interaction with password controls is denied by the MCP boundary.");

        try
        {
            if (element.Patterns.Invoke.IsSupported)
                element.Patterns.Invoke.Pattern.Invoke();
            else
                element.Click();
            return ToolResult<object>.Ok(new { invoked = true, selector = SanitizeSelector(selector) });
        }
        catch (Exception ex) { return ToolResult<object>.Fail("WPF_CLICK_FAILED", SafeException(ex)); }
    }

    public ToolResult<object> TypeText(int processId, UiSelector selector, string text)
    {
        if (redactionService.LooksSensitive(text))
            return ToolResult<object>.Fail("SECRET_INPUT_DENIED", "The supplied text appears to contain a credential or secret. MCP tools do not accept secret material.");
        if (text.Length > 32_768)
            return ToolResult<object>.Fail("INPUT_TOO_LARGE", "Text input exceeds the 32 KiB safety limit.");

        var elementResult = ResolveElement(processId, selector);
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<object>.Fail(elementResult.Error!.Code, elementResult.Error.Message);
        var element = elementResult.Value;

        if (element.Properties.IsPassword.ValueOrDefault)
            return ToolResult<object>.Fail("SENSITIVE_CONTROL_DENIED", "Typing into password controls is denied so secrets never transit the MCP model boundary.");

        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var value = element.Patterns.Value.Pattern;
                if (value.IsReadOnly.ValueOrDefault)
                    return ToolResult<object>.Fail("ELEMENT_READ_ONLY", "Target value is read-only.");
                value.SetValue(text);
            }
            else
            {
                element.Focus();
                FlaUI.Core.Input.Keyboard.Type(text);
            }
            return ToolResult<object>.Ok(new { typed = true, length = text.Length, selector = SanitizeSelector(selector) });
        }
        catch (Exception ex) { return ToolResult<object>.Fail("WPF_TYPE_FAILED", SafeException(ex)); }
    }

    public ToolResult<object> Select(int processId, UiSelector selector, string itemText)
    {
        var elementResult = ResolveElement(processId, selector);
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<object>.Fail(elementResult.Error!.Code, elementResult.Error.Message);
        try
        {
            var element = elementResult.Value;
            if (element.ControlType == ControlType.ComboBox)
            {
                var selected = element.AsComboBox().Select(itemText);
                return selected is null
                    ? ToolResult<object>.Fail("ITEM_NOT_FOUND", "No matching combo-box item was found.")
                    : ToolResult<object>.Ok(new { selected = SafeText(selected.Text) });
            }
            if (element.ControlType == ControlType.List)
            {
                var selected = element.AsListBox().Select(itemText);
                return selected is null
                    ? ToolResult<object>.Fail("ITEM_NOT_FOUND", "No matching list item was found.")
                    : ToolResult<object>.Ok(new { selected = SafeText(selected.Text) });
            }
            return ToolResult<object>.Fail("SELECTION_NOT_SUPPORTED", "Target is not a supported selection control.");
        }
        catch (Exception ex) { return ToolResult<object>.Fail("WPF_SELECT_FAILED", SafeException(ex)); }
    }

    public ToolResult<object> Toggle(int processId, UiSelector selector)
    {
        var elementResult = ResolveElement(processId, selector);
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<object>.Fail(elementResult.Error!.Code, elementResult.Error.Message);
        try
        {
            var element = elementResult.Value;
            if (!element.Patterns.Toggle.IsSupported)
                return ToolResult<object>.Fail("TOGGLE_NOT_SUPPORTED", "Target does not expose the UI Automation Toggle pattern.");
            var pattern = element.Patterns.Toggle.Pattern;
            pattern.Toggle();
            return ToolResult<object>.Ok(new { toggled = true, state = pattern.ToggleState.Value.ToString() });
        }
        catch (Exception ex) { return ToolResult<object>.Fail("WPF_TOGGLE_FAILED", SafeException(ex)); }
    }

    public ToolResult<object> Expand(int processId, UiSelector selector) => SetExpanded(processId, selector, true);
    public ToolResult<object> Collapse(int processId, UiSelector selector) => SetExpanded(processId, selector, false);

    public ToolResult<object> ScrollIntoView(int processId, UiSelector selector)
    {
        var elementResult = ResolveElement(processId, selector);
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<object>.Fail(elementResult.Error!.Code, elementResult.Error.Message);
        try
        {
            var element = elementResult.Value;
            if (!element.Patterns.ScrollItem.IsSupported)
                return ToolResult<object>.Fail("SCROLL_ITEM_NOT_SUPPORTED", "Target does not expose the UI Automation ScrollItem pattern.");
            element.Patterns.ScrollItem.Pattern.ScrollIntoView();
            return ToolResult<object>.Ok(new { scrolledIntoView = true, selector = SanitizeSelector(selector) });
        }
        catch (Exception ex) { return ToolResult<object>.Fail("WPF_SCROLL_FAILED", SafeException(ex)); }
    }

    public ToolResult<object> Focus(int processId, UiSelector selector)
    {
        var elementResult = ResolveElement(processId, selector);
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<object>.Fail(elementResult.Error!.Code, elementResult.Error.Message);
        try
        {
            var element = elementResult.Value;
            if (element.Properties.IsPassword.ValueOrDefault)
                return ToolResult<object>.Fail("SENSITIVE_CONTROL_DENIED", "Focusing password controls through MCP is denied.");
            if (!element.Properties.IsKeyboardFocusable.ValueOrDefault)
                return ToolResult<object>.Fail("ELEMENT_NOT_FOCUSABLE", "Target is not keyboard focusable according to UI Automation.");
            element.Focus();
            return ToolResult<object>.Ok(new { focused = true, selector = SanitizeSelector(selector) });
        }
        catch (Exception ex) { return ToolResult<object>.Fail("WPF_FOCUS_FAILED", SafeException(ex)); }
    }

    public ToolResult<SanitizedScreenshot> Screenshot(int processId, UiSelector? selector = null)
    {
        if (!policyProvider.Current.Screenshots.Enabled)
            return ToolResult<SanitizedScreenshot>.Fail("SCREENSHOTS_DISABLED", "Screenshots are disabled by policy.");

        var elementResult = ResolveElement(processId, selector ?? new UiSelector());
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<SanitizedScreenshot>.Fail(elementResult.Error!.Code, elementResult.Error.Message);

        try
        {
            using var dpiContext = DpiAwarenessScope.EnterPerMonitorV2();
            var target = elementResult.Value;
            var targetRect = target.Properties.BoundingRectangle.ValueOrDefault;
            var captureBounds = new Rectangle(
                (int)Math.Round((double)targetRect.X),
                (int)Math.Round((double)targetRect.Y),
                (int)Math.Round((double)targetRect.Width),
                (int)Math.Round((double)targetRect.Height));
            using var bitmap = CaptureWindowSurface(
                processId,
                captureBounds,
                out var redactionOffsetX,
                out var redactionOffsetY);
            var redactions = 0;
            var descendants = target.FindAllDescendants();
            using var graphics = Graphics.FromImage(bitmap);
            foreach (var candidate in descendants.Prepend(target))
            {
                if (!ShouldMask(candidate)) continue;
                if (ReferenceEquals(candidate, target))
                {
                    graphics.FillRectangle(Brushes.Black, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    redactions++;
                    break;
                }

                var r = candidate.Properties.BoundingRectangle.ValueOrDefault;
                if (r.Width <= 0 || r.Height <= 0)
                {
                    if (candidate.Properties.IsOffscreen.ValueOrDefault)
                        continue;

                    // WPF can expose an empty TextBlock as onscreen even though it has
                    // no renderable pixels (0x0 bounds). It contains nothing that can
                    // leak into the bitmap, so it is safe to ignore. Keep failing closed
                    // for password/edit/document/data-item controls and for any text
                    // element whose UIA name indicates content despite unusable bounds.
                    if (candidate.Properties.ControlType.ValueOrDefault == ControlType.Text &&
                        string.IsNullOrWhiteSpace(candidate.Properties.Name.ValueOrDefault))
                    {
                        continue;
                    }

                    return ToolResult<SanitizedScreenshot>.Fail(
                        "SCREENSHOT_REDACTION_FAILED",
                        "Screenshot was withheld because a visible sensitive UI region had no usable bounds.");
                }

                var rawRelative = new Rectangle(
                    r.X - targetRect.X - 2,
                    r.Y - targetRect.Y - 2,
                    r.Width + 4,
                    r.Height + 4);
                var bitmapBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var applied = false;
                var relative = Rectangle.Intersect(rawRelative, bitmapBounds);
                if (relative.Width > 0 && relative.Height > 0)
                {
                    graphics.FillRectangle(Brushes.Black, relative);
                    applied = true;
                }

                // PrintWindow renders WPF client content after the native frame.
                // UIA coordinates are screen-relative, so mask the corresponding
                // client-offset location as well. Keeping the original mask covers
                // native/non-client providers and mixed-HWND surfaces conservatively.
                if (redactionOffsetX != 0 || redactionOffsetY != 0)
                {
                    var shifted = Rectangle.Intersect(
                        new Rectangle(
                            rawRelative.X + redactionOffsetX,
                            rawRelative.Y + redactionOffsetY,
                            rawRelative.Width,
                            rawRelative.Height),
                        bitmapBounds);
                    if (shifted.Width > 0 && shifted.Height > 0)
                    {
                        graphics.FillRectangle(Brushes.Black, shifted);
                        applied = true;
                    }
                }

                if (applied) redactions++;
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            if (stream.Length > 10 * 1024 * 1024)
                return ToolResult<SanitizedScreenshot>.Fail("SCREENSHOT_TOO_LARGE", "Sanitized screenshot exceeds the 10 MiB MCP payload safety limit.");
            return ToolResult<SanitizedScreenshot>.Ok(new SanitizedScreenshot(
                "image/png",
                Convert.ToBase64String(stream.ToArray()),
                bitmap.Width,
                bitmap.Height,
                redactions,
                "uia-text-and-sensitive-region-mask-v2"));
        }
        catch (Exception ex)
        {
            if (policyProvider.Current.Screenshots.FailClosedOnRedactionError)
                return ToolResult<SanitizedScreenshot>.Fail("SCREENSHOT_REDACTION_FAILED", "Screenshot was withheld because fail-closed redaction could not be guaranteed.");
            return ToolResult<SanitizedScreenshot>.Fail("SCREENSHOT_FAILED", SafeException(ex));
        }
    }

    public ToolResult<object> Detach(int processId)
    {
        if (_sessions.TryRemove(processId, out var session))
        {
            session.Dispose();
            return ToolResult<object>.Ok(new { processId, detached = true });
        }
        return ToolResult<object>.Ok(new { processId, detached = false, reason = "not-attached" });
    }

    private static Bitmap CaptureWindowSurface(
        int processId,
        Rectangle targetRect,
        out int redactionOffsetX,
        out int redactionOffsetY)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        var windowHandle = process.MainWindowHandle;
        if (windowHandle == IntPtr.Zero)
            throw new InvalidOperationException("The target process has no main window handle available for safe capture.");
        if (!NativeMethods.GetWindowRect(windowHandle, out var windowRect))
            throw new InvalidOperationException("Windows did not return target-window bounds for safe capture.");

        var windowWidth = windowRect.Right - windowRect.Left;
        var windowHeight = windowRect.Bottom - windowRect.Top;
        if (windowWidth <= 0 || windowHeight <= 0)
            throw new InvalidOperationException("The target window has no renderable bounds for safe capture.");

        var clientOrigin = new NativePoint();
        if (!NativeMethods.ClientToScreen(windowHandle, ref clientOrigin))
            throw new InvalidOperationException("Windows did not return the target client origin for safe redaction.");
        redactionOffsetX = clientOrigin.X - windowRect.Left;
        redactionOffsetY = clientOrigin.Y - windowRect.Top;

        using var windowBitmap = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(windowBitmap))
        {
            var deviceContext = graphics.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(windowHandle, deviceContext, NativeMethods.PwRenderFullContent))
                    throw new InvalidOperationException("Windows could not render the target window independently of the desktop.");
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }

        var desiredCrop = new Rectangle(
            targetRect.X - windowRect.Left,
            targetRect.Y - windowRect.Top,
            targetRect.Width,
            targetRect.Height);
        var windowBounds = new Rectangle(0, 0, windowBitmap.Width, windowBitmap.Height);
        if (desiredCrop.Width <= 0 || desiredCrop.Height <= 0 ||
            !windowBounds.Contains(desiredCrop))
        {
            throw new InvalidOperationException("The requested UI element is outside the safely rendered main-window surface.");
        }

        return windowBitmap.Clone(desiredCrop, PixelFormat.Format32bppArgb);
    }

    private static class NativeMethods
    {
        internal const uint PwRenderFullContent = 0x00000002;
        internal static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr windowHandle, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    }

    private sealed class DpiAwarenessScope(IntPtr previousContext) : IDisposable
    {
        public static DpiAwarenessScope EnterPerMonitorV2()
        {
            var previous = NativeMethods.SetThreadDpiAwarenessContext(
                NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
            if (previous == IntPtr.Zero)
                throw new InvalidOperationException("Windows could not establish a per-monitor DPI context for safe capture.");
            return new DpiAwarenessScope(previous);
        }

        public void Dispose()
            => NativeMethods.SetThreadDpiAwarenessContext(previousContext);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    private ToolResult<object> SetExpanded(int processId, UiSelector selector, bool expanded)
    {
        var elementResult = ResolveElement(processId, selector);
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<object>.Fail(elementResult.Error!.Code, elementResult.Error.Message);
        try
        {
            var element = elementResult.Value;
            if (!element.Patterns.ExpandCollapse.IsSupported)
                return ToolResult<object>.Fail("EXPAND_COLLAPSE_NOT_SUPPORTED", "Target does not expose the ExpandCollapse pattern.");
            var pattern = element.Patterns.ExpandCollapse.Pattern;
            if (expanded) pattern.Expand(); else pattern.Collapse();
            return ToolResult<object>.Ok(new { expanded, state = pattern.ExpandCollapseState.Value.ToString() });
        }
        catch (Exception ex) { return ToolResult<object>.Fail("WPF_EXPAND_COLLAPSE_FAILED", SafeException(ex)); }
    }

    private ToolResult<UiElementSnapshot> ResolveSnapshot(int processId, UiSelector selector)
    {
        var sessionResult = RequireSession(processId);
        if (!sessionResult.Success || sessionResult.Value is null)
            return ToolResult<UiElementSnapshot>.Fail(sessionResult.Error!.Code, sessionResult.Error.Message);
        var elementResult = ResolveElement(sessionResult.Value, selector);
        if (!elementResult.Success || elementResult.Value is null)
            return ToolResult<UiElementSnapshot>.Fail(elementResult.Error!.Code, elementResult.Error.Message);
        var session = sessionResult.Value;
        var reference = selector.Reference;
        if (string.IsNullOrWhiteSpace(reference)) reference = session.Register(elementResult.Value);
        return ToolResult<UiElementSnapshot>.Ok(ToSnapshot(elementResult.Value, reference, null, 0));
    }

    private ToolResult<AutomationElement> ResolveElement(int processId, UiSelector selector)
    {
        var sessionResult = RequireSession(processId);
        return !sessionResult.Success || sessionResult.Value is null
            ? ToolResult<AutomationElement>.Fail(sessionResult.Error!.Code, sessionResult.Error.Message)
            : ResolveElement(sessionResult.Value, selector);
    }

    private ToolResult<AutomationElement> ResolveElement(AttachedSession session, UiSelector selector)
    {
        if (!string.IsNullOrWhiteSpace(selector.Reference))
        {
            return session.References.TryGetValue(selector.Reference, out var referenced)
                ? ToolResult<AutomationElement>.Ok(referenced)
                : ToolResult<AutomationElement>.Fail("ELEMENT_REFERENCE_NOT_FOUND", "Element reference is unknown or stale.");
        }

        var windows = session.Application.GetAllTopLevelWindows(session.Automation);
        if (selector == new UiSelector())
        {
            var root = session.Application.GetMainWindow(session.Automation) ?? windows.FirstOrDefault();
            return root is null
                ? ToolResult<AutomationElement>.Fail("WPF_WINDOW_NOT_FOUND", "No application window was found.")
                : ToolResult<AutomationElement>.Ok(root);
        }

        foreach (var window in windows)
        {
            var candidates = window.FindAllDescendants();
            foreach (var element in candidates.Prepend(window))
            {
                if (Matches(element, selector)) return ToolResult<AutomationElement>.Ok(element);
            }
        }

        return ToolResult<AutomationElement>.Fail("ELEMENT_NOT_FOUND", "No UI element matched the semantic selector.");
    }

    private static bool Matches(AutomationElement element, UiSelector selector)
    {
        if (!string.IsNullOrWhiteSpace(selector.AutomationId) &&
            !string.Equals(element.Properties.AutomationId.ValueOrDefault, selector.AutomationId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(selector.Name) &&
            !string.Equals(element.Properties.Name.ValueOrDefault, selector.Name, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(selector.ClassName) &&
            !string.Equals(element.Properties.ClassName.ValueOrDefault, selector.ClassName, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(selector.ControlType) &&
            !string.Equals(element.Properties.ControlType.ValueOrDefault.ToString(), selector.ControlType, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private ToolResult<AttachedSession> RequireSession(int processId)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success)
            return ToolResult<AttachedSession>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        allowed.Value?.Dispose();

        if (_sessions.TryGetValue(processId, out var session)) return ToolResult<AttachedSession>.Ok(session);
        var attach = Attach(processId);
        if (!attach.Success) return ToolResult<AttachedSession>.Fail(attach.Error!.Code, attach.Error.Message);
        return _sessions.TryGetValue(processId, out session)
            ? ToolResult<AttachedSession>.Ok(session)
            : ToolResult<AttachedSession>.Fail("WPF_ATTACH_FAILED", "Attach completed without creating a usable session.");
    }

    private UiElementSnapshot ToSnapshot(AutomationElement element, string reference, string? parentReference, int depth)
    {
        IReadOnlyList<string> patterns;
        try { patterns = element.GetSupportedPatterns().Select(p => p.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray(); }
        catch { patterns = Array.Empty<string>(); }

        return new UiElementSnapshot(
            reference,
            parentReference,
            element.Properties.ControlType.ValueOrDefault.ToString(),
            SafeText(element.Properties.Name.ValueOrDefault),
            SafeText(element.Properties.AutomationId.ValueOrDefault),
            SafeText(element.Properties.ClassName.ValueOrDefault),
            SafeText(element.Properties.FrameworkId.ValueOrDefault),
            ToRect(element.Properties.BoundingRectangle.ValueOrDefault),
            element.Properties.IsEnabled.ValueOrDefault,
            element.Properties.IsOffscreen.ValueOrDefault,
            element.Properties.IsKeyboardFocusable.ValueOrDefault,
            element.Properties.IsPassword.ValueOrDefault,
            patterns,
            depth);
    }

    private bool ShouldMask(AutomationElement element)
    {
        if (policyProvider.Current.Screenshots.MaskPasswordControls && element.Properties.IsPassword.ValueOrDefault)
            return true;
        if (policyProvider.Current.Screenshots.MaskTextControls)
        {
            var controlType = element.Properties.ControlType.ValueOrDefault;
            if (controlType == ControlType.Text || controlType == ControlType.Edit ||
                controlType == ControlType.Document || controlType == ControlType.DataItem)
                return true;
        }
        if (!policyProvider.Current.Screenshots.MaskSensitiveNames) return false;
        var name = element.Properties.Name.ValueOrDefault ?? string.Empty;
        var id = element.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        var combined = name + " " + id;
        return SensitiveControlName(combined) || redactionService.LooksSensitiveOrPii(combined);
    }

    private static bool SensitiveControlName(string value)
    {
        string[] terms = ["password", "passwd", "secret", "token", "api key", "apikey", "connectionstring", "connection string", "ssn", "social security", "private key"];
        return terms.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private string SafeText(string? value) => redactionService.Redact(value ?? string.Empty, policyProvider.Current.Pii);
    private string SafeException(Exception ex) => redactionService.Redact(ex.Message, policyProvider.Current.Pii);
    private static RectDto ToRect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
    private static object SanitizeSelector(UiSelector selector) => new { selector.Reference, selector.AutomationId, selector.Name, selector.ControlType, selector.ClassName };

    public void Dispose()
    {
        foreach (var pair in _sessions) pair.Value.Dispose();
        _sessions.Clear();
    }
}
