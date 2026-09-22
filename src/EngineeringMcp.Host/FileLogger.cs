using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace EngineeringMcp.Host;

// ponytail: hand-rolled daily rolling file logger instead of a Serilog dependency.
// Retention prunes files older than seven days; add config knobs only if someone asks.
public sealed class EngineeringFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private FileStream _stream;
    private StreamWriter _sink;
    private string _day;

    public EngineeringFileLoggerProvider()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DotNetEngineeringMcp", "logs");
        Directory.CreateDirectory(_directory);
        PruneOldLogs(_directory);

        // FileShare.ReadWrite: several host processes (HTTP + stdio clients) share this daily file.
        _day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        _stream = new FileStream(Path.Combine(_directory, $"engmcp-{_day}.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _sink = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "engmcp-file-logger" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _writer.Join(TimeSpan.FromSeconds(2)); } catch { /* join timeout on shutdown is non-fatal */ }
        _sink.Dispose();
        _stream.Dispose();
        _queue.Dispose();
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            // Date rollover: rotate to the new day's file and re-run retention so a long-lived
            // host cannot keep writing into (and evading prune on) yesterday's file. Only this
            // writer thread touches the stream, so no additional locking is required.
            var day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (!string.Equals(_day, day, StringComparison.Ordinal))
            {
                try
                {
                    PruneOldLogs(_directory);
                    // Open the new day's stream before disposing the old one; if the open fails,
                    // keep appending to the old stream and let the next line retry the rollover.
                    var newStream = new FileStream(Path.Combine(_directory, $"engmcp-{day}.log"),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    var newSink = new StreamWriter(newStream, Encoding.UTF8) { AutoFlush = true };
                    var oldSink = _sink;
                    _stream = newStream;
                    _sink = newSink;
                    _day = day;
                    oldSink.Dispose();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep appending to the old stream; the next line retries the rollover.
                }
            }
            _sink.WriteLine(line);
        }
    }

    private static void PruneOldLogs(string directory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var file in Directory.EnumerateFiles(directory, "engmcp-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch { /* retention cleanup is best-effort; a locked file must not crash host startup */ }
        }
    }

    private sealed class FileLogger(EngineeringFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            try { provider._queue.TryAdd(line); } catch (InvalidOperationException) { /* shutting down */ }
        }
    }
}