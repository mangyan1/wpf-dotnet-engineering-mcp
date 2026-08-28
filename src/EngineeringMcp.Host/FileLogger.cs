using System.Collections.Concurrent;
using System.Text;

namespace EngineeringMcp.Host;

// ponytail: hand-rolled daily rolling file logger instead of a Serilog dependency.
// Retention prunes files older than seven days; add config knobs only if someone asks.
public sealed class EngineeringFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private readonly FileStream _stream;
    private readonly StreamWriter _sink;

    public EngineeringFileLoggerProvider()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DotNetEngineeringMcp", "logs");
        Directory.CreateDirectory(_directory);
        PruneOldLogs(_directory);

        // FileShare.ReadWrite: several host processes (HTTP + stdio clients) share this daily file.
        _stream = new FileStream(Path.Combine(_directory, $"engmcp-{DateTime.UtcNow:yyyyMMdd}.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _sink = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "engmcp-file-logger" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _writer.Join(TimeSpan.FromSeconds(2)); } catch { /* ponytail: join timeout on shutdown is non-fatal */ }
        _sink.Dispose();
        _stream.Dispose();
        _queue.Dispose();
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
            _sink.WriteLine(line);
    }

    private static void PruneOldLogs(string directory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var file in Directory.EnumerateFiles(directory, "engmcp-*.log"))
            if (File.GetLastWriteTimeUtc(file) < cutoff)
                try { File.Delete(file); } catch (IOException) { }
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