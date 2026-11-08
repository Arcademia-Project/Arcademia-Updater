using System;
using System.Diagnostics;
using System.IO;
using ArcademiaUpdater.Platform;
using Xunit;

namespace ArcademiaUpdater.Tests
{
    public class PlatformTests
    {
        [Fact]
        public void Heartbeat_marks_the_launcher_as_shown_and_responding_on_unix()
        {
            if (OperatingSystem.IsWindows())
                return;

            using var root = new TempDirectory();
            using var signals = new LauncherSignals(root.Path);
            using var self = Process.GetCurrentProcess();
            var heartbeat = root.Combine(LauncherSignals.HeartbeatFileName);

            File.WriteAllText(heartbeat, "stale");
            signals.PrepareForStart();
            Assert.False(signals.HasWindow(self));
            Assert.False(signals.IsResponding(self));

            File.WriteAllText(heartbeat, "");
            Assert.True(signals.HasWindow(self));
            Assert.True(signals.IsResponding(self));

            File.SetLastWriteTimeUtc(heartbeat, DateTime.UtcNow.AddMinutes(-1));
            Assert.True(signals.HasWindow(self));
            Assert.False(signals.IsResponding(self));
        }

        [Fact]
        public void Close_request_sends_sigterm_on_unix()
        {
            if (OperatingSystem.IsWindows())
                return;

            using var root = new TempDirectory();
            using var signals = new LauncherSignals(root.Path);
            using var sleeper = Process.Start("sleep", "30");

            signals.RequestClose(sleeper);

            Assert.True(sleeper.WaitForExit(5000));
        }

        [Fact]
        public void Close_request_sets_the_named_event_on_windows()
        {
            if (!OperatingSystem.IsWindows())
                return;

            using var root = new TempDirectory();
            using var signals = new LauncherSignals(root.Path);
            using var listener = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, LauncherSignals.CloseRequestEventName);
            using var self = Process.GetCurrentProcess();

            signals.RequestClose(self);

            Assert.True(listener.WaitOne(1000));
        }

        [Fact]
        public void Version_label_uses_the_project_version()
        {
            Assert.Matches(@"^\[v\d+\.\d+\.\d+(-[0-9A-Za-z.]+)? Updater Release Build\]$", AppInfo.VersionLabel);
        }

        [Theory]
        [InlineData("2.10.0", "2.9.9", 1)]
        [InlineData("2.10.0", "2.10.0", 0)]
        [InlineData("1.0.0", "1.0.1", -1)]
        [InlineData("bad", "0.0.0", 0)]
        public void Versions_compare_numerically(string a, string b, int expected)
        {
            Assert.Equal(expected, Math.Sign(new Classes.Version(a).CompareTo(new Classes.Version(b))));
        }
    }
}
