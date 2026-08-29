using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.Probe.Wpf;

public sealed record WpfProbeOptions(
    string? Token = null,
    string? PipeName = null,
    int MaxTreeElements = 2_000);

public static class WpfProbe
{
    private static readonly object Sync = new();
    private static WpfProbeServer? _server;

    public static IDisposable Start(WpfProbeOptions? options = null)
    {
        lock (Sync)
        {
            if (_server is not null) return new ProbeLease(_server);
            options ??= new WpfProbeOptions();
            var token = options.Token ?? Environment.GetEnvironmentVariable("ENGINEERING_MCP_PROBE_TOKEN");
            if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException("WPF probe requires ENGINEERING_MCP_PROBE_TOKEN with at least 32 characters.");
            _server = new WpfProbeServer(options with { Token = token });
            _server.Start();
            return new ProbeLease(_server);
        }
    }

    private sealed class ProbeLease(WpfProbeServer server) : IDisposable
    {
        private WpfProbeServer? _server = server;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _server, null);
            if (current is null) return;
            lock (Sync)
            {
                if (!ReferenceEquals(WpfProbe._server, current)) return;
                WpfProbe._server = null;
                current.Dispose();
            }
        }
    }
}

internal sealed class WpfProbeServer : IDisposable
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "IsEnabled", "Visibility", "ActualWidth", "ActualHeight", "Width", "Height", "MinWidth", "MinHeight",
        "MaxWidth", "MaxHeight", "Margin", "Padding", "Background", "Foreground", "BorderBrush", "BorderThickness",
        "FontFamily", "FontSize", "FontWeight", "FontStyle", "HorizontalAlignment", "VerticalAlignment", "Opacity",
        "ToolTip", "Tag", "Style"
    };

    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DispatcherTimeout = TimeSpan.FromSeconds(5);
    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly WpfProbeOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly RedactionService _redactor = new();
    private readonly ConcurrentQueue<WpfExceptionObservation> _exceptions = new();
    private Task? _loop;

    public WpfProbeServer(WpfProbeOptions options)
        => _options = options with { MaxTreeElements = Math.Clamp(options.MaxTreeElements, 10, 10_000) };

    public void Start()
    {
        var app = Application.Current;
        if (app is not null) app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                ProbeResponse response;
                try
                {
                    var request = await BoundedJsonPipeProtocol.ReadAsync<ProbeRequest>(pipe, MaxRequestBytes, _cts.Token)
                        .AsTask()
                        .WaitAsync(RequestReadTimeout, _cts.Token)
                        .ConfigureAwait(false);
                    response = request is null
                        ? new ProbeResponse(false, ErrorCode: "INVALID_REQUEST", ErrorMessage: "Request was empty.")
                        : await DispatchAsync(request).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    response = new ProbeResponse(false, ErrorCode: "PROBE_REQUEST_TIMEOUT", ErrorMessage: "Probe request was not received within the allowed time.");
                }
                catch (JsonException)
                {
                    response = new ProbeResponse(false, ErrorCode: "INVALID_JSON", ErrorMessage: "Request JSON was invalid.");
                }
                catch (Exception ex)
                {
                    response = new ProbeResponse(false, ErrorCode: "PROBE_ERROR", ErrorMessage: _redactor.Redact(ex.Message));
                }

                await BoundedJsonPipeProtocol.WriteAsync(pipe, response, MaxResponseBytes, _cts.Token)
                    .AsTask()
                    .WaitAsync(RequestReadTimeout, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (TimeoutException) { await DelayAfterTransportFaultAsync().ConfigureAwait(false); }
            catch (IOException) { await DelayAfterTransportFaultAsync().ConfigureAwait(false); }
            catch { await DelayAfterTransportFaultAsync().ConfigureAwait(false); }
        }
    }

    private async Task<ProbeResponse> DispatchAsync(ProbeRequest request)
    {
        if (!FixedTimeTokenEquals(request.Token, _options.Token!))
            return new ProbeResponse(false, ErrorCode: "AUTH_FAILED", ErrorMessage: "Probe authentication failed.");

        var app = Application.Current;
        if (app is null)
            return new ProbeResponse(false, ErrorCode: "WPF_APP_UNAVAILABLE", ErrorMessage: "Application.Current is unavailable.");

        // Status is intentionally dispatcher-independent. This lets the host distinguish an alive probe
        // from a blocked WPF dispatcher and prevents a health check from being held hostage by UI work.
        if (string.Equals(request.Operation, "status", StringComparison.Ordinal))
        {
            return new ProbeResponse(true, new
            {
                processId = Environment.ProcessId,
                pipe = PipeName,
                dispatcherAccess = app.Dispatcher.CheckAccess(),
                dispatcherShutdownStarted = app.Dispatcher.HasShutdownStarted,
                dispatcherShutdownFinished = app.Dispatcher.HasShutdownFinished
            });
        }

        try
        {
            var operation = app.Dispatcher.InvokeAsync(() => ExecuteOnDispatcher(request));
            return await operation.Task
                .WaitAsync(DispatcherTimeout, _cts.Token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ProbeResponse(false, ErrorCode: "WPF_DISPATCHER_TIMEOUT", ErrorMessage: "The WPF dispatcher did not service the probe request within 5 seconds.");
        }
    }

    private ProbeResponse ExecuteOnDispatcher(ProbeRequest request)
    {
        return request.Operation switch
        {
            "status" => new ProbeResponse(true, new { processId = Environment.ProcessId, pipe = PipeName, dispatcherAccess = Application.Current!.Dispatcher.CheckAccess() }),
            "visual_tree" or "visualTree" => VisualTree(request),
            "logical_tree" or "logicalTree" => LogicalTree(request),
            "datacontext" => DataContext(request),
            "binding" => Binding(request),
            "binding_errors" => BindingErrors(request),
            "command" => Command(request),
            "validation" => ValidationErrors(request),
            "validation_summary" => ValidationSummary(request),
            "resource" => Resource(request),
            "property" => Property(request),
            "dispatcher" => new ProbeResponse(true, new { threadId = Environment.CurrentManagedThreadId, hasAccess = Application.Current!.Dispatcher.CheckAccess(), shutdownStarted = Application.Current!.Dispatcher.HasShutdownStarted }),
            "exceptions" => new ProbeResponse(true, _exceptions.Reverse().Take(100).Reverse().ToArray()),
            _ => new ProbeResponse(false, ErrorCode: "OPERATION_NOT_ALLOWED", ErrorMessage: "Requested probe operation is not in the allowlist.")
        };
    }

    private ProbeResponse VisualTree(ProbeRequest request)
    {
        var root = Resolve(request);
        if (root is null) return NotFound();
        var items = new List<object>();
        var queue = new Queue<(DependencyObject Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && items.Count < _options.MaxTreeElements)
        {
            var (node, depth) = queue.Dequeue();
            items.Add(SafeElementDescriptor(node, depth));
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) queue.Enqueue((VisualTreeHelper.GetChild(node, i), depth + 1));
        }
        return new ProbeResponse(true, new { items, truncated = queue.Count > 0 });
    }

    private ProbeResponse LogicalTree(ProbeRequest request)
    {
        var root = Resolve(request);
        if (root is null) return NotFound();
        var items = new List<object>();
        var queue = new Queue<(DependencyObject Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && items.Count < _options.MaxTreeElements)
        {
            var (node, depth) = queue.Dequeue();
            items.Add(SafeElementDescriptor(node, depth));
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) queue.Enqueue((child, depth + 1));
        }
        return new ProbeResponse(true, new { items, truncated = queue.Count > 0 });
    }

    private ProbeResponse DataContext(ProbeRequest request)
    {
        var element = Resolve(request) as FrameworkElement;
        if (element is null) return NotFound();
        var dc = element.DataContext;
        return new ProbeResponse(true, new
        {
            element = DescribeIdentity(element),
            dataContextType = dc?.GetType().FullName,
            isNull = dc is null
        });
    }

    private ProbeResponse Binding(ProbeRequest request)
    {
        var element = Resolve(request) as FrameworkElement;
        if (element is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Property))
            return new ProbeResponse(false, ErrorCode: "PROPERTY_REQUIRED", ErrorMessage: "A dependency property name is required.");
        if (!AllowedProperties.Contains(request.Property))
            return new ProbeResponse(false, ErrorCode: "PROPERTY_NOT_ALLOWED", ErrorMessage: "Property is outside the probe allowlist.");

        var dp = FindDependencyProperty(element, request.Property);
        if (dp is null) return new ProbeResponse(false, ErrorCode: "DEPENDENCY_PROPERTY_NOT_FOUND", ErrorMessage: "Dependency property was not found.");
        var expression = BindingOperations.GetBindingExpressionBase(element, dp);
        if (expression is null) return new ProbeResponse(true, new { bound = false, property = request.Property });

        string? path = null;
        string? mode = null;
        string? update = null;
        if (expression.ParentBindingBase is System.Windows.Data.Binding b)
        {
            path = b.Path?.Path;
            mode = b.Mode.ToString();
            update = b.UpdateSourceTrigger.ToString();
        }
        return new ProbeResponse(true, new { bound = true, property = request.Property, path, mode, update, status = expression.Status.ToString() });
    }

    private ProbeResponse BindingErrors(ProbeRequest request)
    {
        var root = Resolve(request);
        if (root is null) return NotFound();
        var findings = new List<BindingDiagnostic>();
        foreach (var node in EnumerateVisual(root, _options.MaxTreeElements))
        {
            if (node is not FrameworkElement fe) continue;
            var values = fe.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                var entry = values.Current;
                var expression = BindingOperations.GetBindingExpressionBase(fe, entry.Property);
                if (expression is null || !expression.HasError) continue;
                string? path = null;
                if (expression.ParentBindingBase is System.Windows.Data.Binding b) path = b.Path?.Path;
                findings.Add(new BindingDiagnostic(DescribeIdentity(fe), entry.Property.Name, path, expression.Status.ToString(), "Binding expression reports an error."));
            }
        }
        return new ProbeResponse(true, findings);
    }

    private ProbeResponse Command(ProbeRequest request)
    {
        var element = Resolve(request);
        if (element is null) return NotFound();
        ICommand? command = element switch
        {
            ButtonBase button => button.Command,
            MenuItem menu => menu.Command,
            _ => null
        };
        return new ProbeResponse(true, new
        {
            element = DescribeIdentity(element),
            commandType = command?.GetType().FullName,
            hasCommand = command is not null,
            isEnabled = element is UIElement ui && ui.IsEnabled,
            note = "CanExecute is not invoked by the probe because arbitrary command evaluation may have application side effects."
        });
    }

    private ProbeResponse ValidationErrors(ProbeRequest request)
    {
        var root = Resolve(request);
        if (root is null) return NotFound();
        var list = new List<object>();
        foreach (var node in EnumerateVisual(root, _options.MaxTreeElements))
        {
            if (node is not FrameworkElement fe || !Validation.GetHasError(fe)) continue;
            foreach (var error in Validation.GetErrors(fe))
            {
                list.Add(new
                {
                    element = DescribeIdentity(fe),
                    rule = error.RuleInError?.GetType().FullName,
                    content = SafeDisplay(error.ErrorContent)
                });
            }
        }
        return new ProbeResponse(true, list);
    }

    private ProbeResponse ValidationSummary(ProbeRequest request)
    {
        var root = Resolve(request);
        if (root is null) return NotFound();
        var affectedElements = 0;
        var errorCount = 0;
        var ruleTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in EnumerateVisual(root, _options.MaxTreeElements))
        {
            if (node is not FrameworkElement fe || !Validation.GetHasError(fe)) continue;
            affectedElements++;
            foreach (var error in Validation.GetErrors(fe))
            {
                errorCount++;
                if (error.RuleInError?.GetType().FullName is { Length: > 0 } ruleType)
                    ruleTypes.Add(ruleType);
            }
        }
        return new ProbeResponse(true, new
        {
            errorCount,
            affectedElements,
            ruleTypes = ruleTypes.OrderBy(value => value, StringComparer.Ordinal).Take(32).ToArray(),
            metadataOnly = true
        });
    }

    private ProbeResponse Resource(ProbeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResourceKey))
            return new ProbeResponse(false, ErrorCode: "RESOURCE_KEY_REQUIRED", ErrorMessage: "Resource key is required.");
        var element = Resolve(request) as FrameworkElement;
        if (element is null) return NotFound();

        var observation = FindResourceOrigin(element, request.ResourceKey);
        return observation is null
            ? new ProbeResponse(false, ErrorCode: "RESOURCE_NOT_FOUND", ErrorMessage: "Resource key was not found in the element/application resource chain.")
            : new ProbeResponse(true, observation);
    }

    private ProbeResponse Property(ProbeRequest request)
    {
        var element = Resolve(request) as FrameworkElement;
        if (element is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Property) || !AllowedProperties.Contains(request.Property))
            return new ProbeResponse(false, ErrorCode: "PROPERTY_NOT_ALLOWED", ErrorMessage: "Property is absent or outside the explicit allowlist.");
        if (element is PasswordBox)
            return new ProbeResponse(false, ErrorCode: "SENSITIVE_CONTROL_DENIED", ErrorMessage: "Password controls are not introspected.");

        var property = element.GetType().GetProperty(request.Property, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (property is null || property.GetIndexParameters().Length != 0)
            return new ProbeResponse(false, ErrorCode: "PROPERTY_NOT_FOUND", ErrorMessage: "Allowed property was not present on the element.");
        var value = property.GetValue(element);
        return new ProbeResponse(true, new { element = DescribeIdentity(element), property = request.Property, value = SafeDisplay(value), valueType = value?.GetType().FullName });
    }

    private DependencyObject? Resolve(ProbeRequest request)
    {
        foreach (Window window in Application.Current!.Windows)
        {
            foreach (var node in EnumerateVisual(window, _options.MaxTreeElements))
            {
                if (node is not FrameworkElement fe) continue;
                if (!string.IsNullOrWhiteSpace(request.AutomationId) && string.Equals(AutomationProperties.GetAutomationId(fe), request.AutomationId, StringComparison.Ordinal)) return fe;
                if (!string.IsNullOrWhiteSpace(request.Name) && string.Equals(fe.Name, request.Name, StringComparison.Ordinal)) return fe;
            }
            if (string.IsNullOrWhiteSpace(request.AutomationId) && string.IsNullOrWhiteSpace(request.Name)) return window;
        }
        return null;
    }

    private static IEnumerable<DependencyObject> EnumerateVisual(DependencyObject root, int max)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        var count = 0;
        while (queue.Count > 0 && count++ < max)
        {
            var current = queue.Dequeue();
            yield return current;
            var children = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < children; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
    }

    private object SafeElementDescriptor(DependencyObject node, int depth)
    {
        if (node is FrameworkElement fe)
        {
            return new
            {
                type = node.GetType().FullName,
                name = _redactor.Redact(fe.Name ?? string.Empty),
                automationId = _redactor.Redact(AutomationProperties.GetAutomationId(fe) ?? string.Empty),
                automationName = _redactor.Redact(AutomationProperties.GetName(fe) ?? string.Empty),
                depth,
                width = fe.ActualWidth,
                height = fe.ActualHeight,
                isEnabled = fe.IsEnabled,
                visibility = fe.Visibility.ToString()
            };
        }
        return new { type = node.GetType().FullName, depth };
    }

    private string DescribeIdentity(DependencyObject node)
    {
        if (node is FrameworkElement fe)
        {
            var automationId = AutomationProperties.GetAutomationId(fe);
            if (!string.IsNullOrWhiteSpace(automationId)) return $"AutomationId:{_redactor.Redact(automationId)}";
            if (!string.IsNullOrWhiteSpace(fe.Name)) return $"Name:{_redactor.Redact(fe.Name)}";
        }
        return node.GetType().Name;
    }

    private static DependencyProperty? FindDependencyProperty(DependencyObject element, string propertyName)
    {
        var field = element.GetType().GetField(propertyName + "Property", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
        return field?.GetValue(null) as DependencyProperty;
    }

    private WpfResourceObservation? FindResourceOrigin(FrameworkElement element, string key)
    {
        for (FrameworkElement? current = element; current is not null; current = current.Parent as FrameworkElement)
        {
            if (current.Resources.Contains(key)) return ResourceObservation(key, "element-or-ancestor", current.Resources, current.Resources[key]);
        }

        var app = Application.Current;
        if (app is null) return null;
        if (app.Resources.Contains(key)) return ResourceObservation(key, "application", app.Resources, app.Resources[key]);
        return FindInMerged(key, app.Resources.MergedDictionaries, "application-merged");
    }

    private WpfResourceObservation? FindInMerged(string key, IEnumerable<ResourceDictionary> dictionaries, string scope)
    {
        foreach (var dictionary in dictionaries.Reverse())
        {
            var nested = FindInMerged(key, dictionary.MergedDictionaries, scope);
            if (nested is not null) return nested;
            if (dictionary.Contains(key)) return ResourceObservation(key, scope, dictionary, dictionary[key]);
        }
        return null;
    }

    private WpfResourceObservation ResourceObservation(string key, string scope, ResourceDictionary dictionary, object? value)
        => new(key, scope, dictionary.Source?.ToString(), value?.GetType().FullName, SafeDisplay(value));

    private string? SafeDisplay(object? value) => value switch
    {
        null => null,
        Brush brush => BrushDisplay(brush),
        Thickness t => $"{t.Left},{t.Top},{t.Right},{t.Bottom}",
        CornerRadius c => $"{c.TopLeft},{c.TopRight},{c.BottomRight},{c.BottomLeft}",
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool b => b.ToString(),
        Enum e => e.ToString(),
        string s => _redactor.Redact(s),
        _ => $"<{value.GetType().FullName}>"
    };

    private static string BrushDisplay(Brush brush) => brush switch
    {
        SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        _ => $"<{brush.GetType().Name}>"
    };

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        => RecordException("WPF.DispatcherUnhandledException", e.Exception);

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) RecordException("AppDomain.UnhandledException", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => RecordException("TaskScheduler.UnobservedTaskException", e.Exception);

    private void RecordException(string source, Exception ex)
    {
        _exceptions.Enqueue(new WpfExceptionObservation(
            DateTimeOffset.UtcNow,
            source,
            _redactor.Redact(ex.GetType().FullName ?? ex.GetType().Name),
            Truncate(_redactor.Redact(ex.Message), 4_096),
            Truncate(_redactor.Redact(ex.StackTrace ?? string.Empty), 16_384)));
        while (_exceptions.Count > 100 && _exceptions.TryDequeue(out _)) { }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static ProbeResponse NotFound() => new(false, ErrorCode: "ELEMENT_NOT_FOUND", ErrorMessage: "Requested WPF element was not found.");

    private string PipeName => _options.PipeName ?? $"EngineeringMcp.WpfProbe.{Environment.ProcessId}";

    private static bool FixedTimeTokenEquals(string supplied, string expected)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(supplied ?? string.Empty);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private async Task DelayAfterTransportFaultAsync()
    {
        try
        {
            await Task.Delay(100, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        var app = Application.Current;
        if (app is not null) app.DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
