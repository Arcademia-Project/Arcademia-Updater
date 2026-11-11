using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ArcademiaUpdater.Platform
{
    internal sealed partial class LauncherSignals : IDisposable
    {
        public const string CloseRequestEventName = @"Local\Arcademia.Launcher.CloseRequested";
        public const string HeartbeatFileName = ".arcademia-launcher.heartbeat";

        private const int SIGTERM = 15;
        private static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(20);

        private readonly EventWaitHandle _closeRequest;
        private readonly string _heartbeatPath;

        public LauncherSignals(string rootPath)
        {
            _heartbeatPath = Path.Combine(rootPath, HeartbeatFileName);

            if (OperatingSystem.IsWindows())
            {
                _closeRequest = new EventWaitHandle(false, EventResetMode.AutoReset, CloseRequestEventName);
                _closeRequest.Reset();
            }
        }

        [LibraryImport("User32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int kill(int pid, int sig);

        public void PrepareForStart()
        {
            ResetCloseRequest();

            if (!OperatingSystem.IsWindows())
                try
                {
                    File.Delete(_heartbeatPath);
                }
                catch { }
        }

        public void ResetCloseRequest() => _closeRequest?.Reset();

        public void RequestClose(Process process)
        {
            if (OperatingSystem.IsWindows())
            {
                _closeRequest.Set();
                return;
            }

            kill(process.Id, SIGTERM);
        }

        public bool HasWindow(Process process)
        {
            if (OperatingSystem.IsWindows())
                return process.MainWindowHandle != IntPtr.Zero;

            return File.Exists(_heartbeatPath);
        }

        public bool IsResponding(Process process)
        {
            if (OperatingSystem.IsWindows())
                return process.Responding;

            try
            {
                return DateTime.UtcNow - File.GetLastWriteTimeUtc(_heartbeatPath) < HeartbeatStaleAfter;
            }
            catch
            {
                return false;
            }
        }

        public void BringToFront(Process process)
        {
            if (OperatingSystem.IsWindows())
                SetForegroundWindow(process.MainWindowHandle);
            else
                X11.Activate(process.Id);
        }

        public void Dispose() => _closeRequest?.Dispose();
    }
}
