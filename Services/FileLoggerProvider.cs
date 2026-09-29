using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Research_Arcade_Updater.Services
{
    public sealed class FileLoggerProvider(string directory) : ILoggerProvider
    {
        private readonly string _directory = directory;
        private readonly object _gate = new();

        public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

        public void Dispose() { }

        private void Write(string line)
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    File.AppendAllText(
                        Path.Combine(_directory, $"Updater-{DateTime.Now:yyyyMMdd}.log"),
                        line + Environment.NewLine
                    );
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
                logLevel >= (_isFramework ? LogLevel.Warning : LogLevel.Information);

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

                provider.Write(line);
            }
        }
    }
}
