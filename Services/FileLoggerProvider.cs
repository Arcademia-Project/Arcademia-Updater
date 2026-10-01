using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Research_Arcade_Updater.Services
{
    public sealed class FileLoggerProvider(string directory) : ILoggerProvider
    {
        private static readonly TimeSpan NormalRetention = TimeSpan.FromDays(10);
        private static readonly TimeSpan DebugRetention = TimeSpan.FromDays(3);

        private readonly string _directory = directory;
        private readonly string _debugDirectory = Path.Combine(directory, "Debug");
        private readonly object _gate = new();
        private DateTime _cleanedOn;

        public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

        public void Dispose() { }

        private void Write(string line, bool normal)
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    Directory.CreateDirectory(_debugDirectory);

                    if (_cleanedOn != DateTime.Today)
                    {
                        _cleanedOn = DateTime.Today;
                        Cleanup(_directory, "Updater-*.log", NormalRetention);
                        Cleanup(_debugDirectory, "Updater-Debug-*.log", DebugRetention);
                    }

                    if (normal)
                        File.AppendAllText(
                            Path.Combine(_directory, $"Updater-{DateTime.Now:yyyyMMdd}.log"),
                            line + Environment.NewLine
                        );

                    File.AppendAllText(
                        Path.Combine(_debugDirectory, $"Updater-Debug-{DateTime.Now:yyyyMMdd}.log"),
                        line + Environment.NewLine
                    );
                }
                catch { }
            }
        }

        private static void Cleanup(string directory, string pattern, TimeSpan retention)
        {
            var cutoff = DateTime.Now - retention;
            foreach (var file in Directory.GetFiles(directory, pattern))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }

        private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
        {
            private readonly bool _isFramework =
                category.StartsWith("Microsoft", StringComparison.Ordinal)
                || category.StartsWith("System", StringComparison.Ordinal);

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                logLevel >= (_isFramework ? LogLevel.Information : LogLevel.Debug);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter
            )
            {
                if (!IsEnabled(logLevel))
                    return;

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}";
                if (exception != null)
                    line += Environment.NewLine + exception;

                provider.Write(
                    line,
                    logLevel >= (_isFramework ? LogLevel.Warning : LogLevel.Information)
                );
            }
        }
    }
}
