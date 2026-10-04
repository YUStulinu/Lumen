using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Lumen.App.Infrastructure;

/// <summary>
/// A minimal Microsoft.Extensions.Logging provider that writes one file per day.
/// </summary>
/// <remarks>
/// Logging must never slow down the code that logs (some of it runs inside a keyboard hook's
/// dispatch or the token loop), so <see cref="ILogger.Log"/> only formats the line and drops
/// it into a channel; a single background task does all the disk I/O. Old files are deleted
/// after <see cref="RetentionDays"/> days.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 7;

    private readonly string _directory;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        await foreach (string line in _queue.Reader.ReadAllAsync())
        {
            try
            {
                string path = Path.Combine(_directory, $"lumen-{DateTime.Now:yyyyMMdd}.log");
                var batch = new StringBuilder(line);
                while (_queue.Reader.TryRead(out string? next)) // batch everything already queued into one write
                {
                    batch.Append(next);
                }

                await File.AppendAllTextAsync(path, batch.ToString());
            }
            catch (IOException)
            {
                // Another process (an editor, antivirus) may hold the file briefly; losing a line is acceptable.
            }
        }
    }

    private void DeleteOldFiles()
    {
        foreach (string file in Directory.EnumerateFiles(_directory, "lumen-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-RetentionDays))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        // "Lumen.App.Services.HotkeyService" → "HotkeyService": shorter, still unambiguous here.
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ').Append(Abbreviate(logLevel))
                .Append(' ').Append(_shortCategory)
                .Append(": ").Append(formatter(state, exception))
                .AppendLine();
            if (exception is not null)
            {
                line.AppendLine(exception.ToString());
            }

            provider.Enqueue(line.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
