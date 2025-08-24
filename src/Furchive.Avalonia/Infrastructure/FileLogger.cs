using Microsoft.Extensions.Logging;
using System.Text;
using System.IO;

namespace Furchive.Avalonia.Infrastructure;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_path, categoryName, _lock);

    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _path;
    private readonly string _category;
    private readonly object _lock;

    public FileLogger(string path, string category, object syncLock)
    {
        _path = path;
        _category = category;
        _lock = syncLock;
    }

    IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (formatter == null) return;
        var msg = formatter(state, exception);
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("O")).Append("] ");
        sb.Append('[').Append(logLevel).Append("] ");
        sb.Append('[').Append(_category).Append("] ");
        if (eventId.Id != 0)
        {
            sb.Append("(Event ").Append(eventId.Id).Append(") ");
        }
        sb.AppendLine(msg);
        if (exception != null)
        {
            sb.AppendLine(exception.ToString());
        }
        var line = sb.ToString();
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
                }
                try { File.AppendAllText(_path, line); } catch { /* ignore */ }
            }
        }
        catch
        {
            // never let logging crash the app
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
