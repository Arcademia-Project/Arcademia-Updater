using System;
using System.IO;
using System.Linq;
using ArcademiaUpdater.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcademiaUpdater.Tests
{
    public class LauncherInstallerTests : IDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly LauncherInstaller _installer;

        public LauncherInstallerTests()
        {
            _installer = new LauncherInstaller(NullLogger<LauncherInstaller>.Instance, _root.Path);
        }

        public void Dispose() => _root.Dispose();

        private string Package(string version, params (string, string)[] extra) =>
            Packages.Create(_installer.GetPackagePath(version), Packages.Launcher(version, extra));

        [Fact]
        public void Missing_install_is_reported()
        {
            Assert.Equal(InstallHealth.Missing, _installer.CheckHealth(out _));
            Assert.Null(_installer.ReadManifest());
        }

        [Fact]
        public void Install_writes_files_and_a_verified_manifest()
        {
            _installer.Install(Package("2.10.2", ("Assets/logo.png", "png")), "2.10.2");

            Assert.Equal(InstallHealth.Verified, _installer.CheckHealth(out var reason));
            Assert.Null(reason);
            Assert.Equal("2.10.2", _installer.ReadManifest().Version);
            Assert.Equal(3, _installer.ReadManifest().Files.Count);
            Assert.True(File.Exists(_installer.LauncherExePath));
            Assert.True(File.Exists(_root.Combine("Launcher", "Assets", "logo.png")));
            Assert.False(Directory.Exists(_root.Combine("Launcher.staging")));
        }

        [Fact]
        public void Launcher_executable_is_marked_executable_on_unix()
        {
            if (OperatingSystem.IsWindows())
                return;

            _installer.Install(Package("2.10.2"), "2.10.2");

            var mode = File.GetUnixFileMode(_installer.LauncherExePath);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
        }

        [Fact]
        public void Lost_executable_bit_is_restored_by_the_health_check()
        {
            if (OperatingSystem.IsWindows())
                return;

            _installer.Install(Package("2.10.2"), "2.10.2");
            File.SetUnixFileMode(_installer.LauncherExePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Equal(InstallHealth.Verified, _installer.CheckHealth(out _));
            Assert.True(File.GetUnixFileMode(_installer.LauncherExePath).HasFlag(UnixFileMode.UserExecute));
        }

        [Fact]
        public void Tampered_file_makes_the_install_corrupt()
        {
            _installer.Install(Package("2.10.2"), "2.10.2");
            File.WriteAllText(_root.Combine("Launcher", "Arcademia-Launcher.dll"), "assembly 2.10.X");

            Assert.Equal(InstallHealth.Corrupt, _installer.CheckHealth(out var reason));
            Assert.Contains("Arcademia-Launcher.dll", reason);
        }

        [Fact]
        public void Deleted_file_makes_the_install_corrupt()
        {
            _installer.Install(Package("2.10.2"), "2.10.2");
            File.Delete(_root.Combine("Launcher", "Arcademia-Launcher.dll"));

            Assert.Equal(InstallHealth.Corrupt, _installer.CheckHealth(out var reason));
            Assert.StartsWith("Missing file", reason);
        }

        [Fact]
        public void Launcher_without_manifest_is_legacy()
        {
            Directory.CreateDirectory(_root.Combine("Launcher"));
            File.WriteAllText(_root.Combine("Launcher", LauncherInstaller.LauncherExeName), "old");

            Assert.Equal(InstallHealth.Legacy, _installer.CheckHealth(out _));
        }

        [Fact]
        public void Upgrade_removes_files_the_new_version_no_longer_ships()
        {
            _installer.Install(Package("2.10.1", ("Old.dll", "old")), "2.10.1");
            _installer.Install(Package("2.10.2"), "2.10.2");

            Assert.False(File.Exists(_root.Combine("Launcher", "Old.dll")));
            Assert.Equal(InstallHealth.Verified, _installer.CheckHealth(out _));
            Assert.Equal("2.10.2", _installer.ReadManifest().Version);
        }

        [Fact]
        public void Machine_data_in_a_package_is_never_installed_over_existing_data()
        {
            Directory.CreateDirectory(_root.Combine("Launcher", "Games"));
            File.WriteAllText(_root.Combine("Launcher", "Games", "game.txt"), "machine");
            File.WriteAllText(_root.Combine("Launcher", "ControllerMapping.json"), "machine");

            _installer.Install(
                Package("2.10.2", ("Games/game.txt", "package"), ("ControllerMapping.json", "package"), ("Config.json", "package")),
                "2.10.2"
            );

            Assert.Equal("machine", File.ReadAllText(_root.Combine("Launcher", "Games", "game.txt")));
            Assert.Equal("machine", File.ReadAllText(_root.Combine("Launcher", "ControllerMapping.json")));
            Assert.False(File.Exists(_root.Combine("Launcher", "Config.json")));
        }

        [Fact]
        public void Package_without_the_launcher_is_rejected()
        {
            var zip = Packages.Create(_root.Combine("bad.zip"), new System.Collections.Generic.Dictionary<string, string> { ["readme.txt"] = "x" });

            Assert.False(_installer.IsValidPackage(zip));
            Assert.Throws<InvalidDataException>(() => _installer.Install(zip, "1.0.0"));
        }

        [Fact]
        public void Package_with_an_escaping_path_is_rejected()
        {
            var files = Packages.Launcher("1.0.0", ("../evil.txt", "x"));
            var zip = Packages.Create(_root.Combine("evil.zip"), files);

            var error = Assert.Throws<InvalidDataException>(() => _installer.ValidatePackage(zip));
            Assert.Contains("unsafe path", error.Message);
        }

        [Fact]
        public void Truncated_package_is_rejected()
        {
            var zip = Package("2.10.2");
            var bytes = File.ReadAllBytes(zip);
            File.WriteAllBytes(zip, bytes[..(bytes.Length / 2)]);

            Assert.False(_installer.IsValidPackage(zip));
            Assert.Null(_installer.FindValidCachedPackage("2.10.2"));
        }

        [Fact]
        public void Newest_valid_cached_package_wins_by_semantic_version()
        {
            Package("2.9.0");
            Package("2.10.0");
            var broken = Package("2.11.0");
            File.WriteAllText(broken, "not a zip");

            var (path, version) = _installer.FindNewestValidCachedPackage();

            Assert.Equal("2.10.0", version);
            Assert.Equal(_installer.GetPackagePath("2.10.0"), path);
        }

        [Fact]
        public void Accepting_a_download_keeps_only_that_package()
        {
            Package("2.10.1");
            var part = _installer.PrepareDownloadPath("2.10.2");
            Packages.Create(part, Packages.Launcher("2.10.2"));

            var accepted = _installer.AcceptDownload(part, "2.10.2");

            Assert.Equal(_installer.GetPackagePath("2.10.2"), accepted);
            Assert.Single(Directory.GetFiles(_root.Combine("Packages")));
        }

        [Fact]
        public void Quarantine_moves_session_state_aside()
        {
            Directory.CreateDirectory(_root.Combine("Launcher", "Achievements"));
            File.WriteAllText(_root.Combine("Launcher", "session_current.json"), "{}");
            File.WriteAllText(_root.Combine("Launcher", "Achievements", "cache.json"), "{}");

            _installer.QuarantineState();

            Assert.False(File.Exists(_root.Combine("Launcher", "session_current.json")));
            Assert.False(Directory.Exists(_root.Combine("Launcher", "Achievements")));
            var recovery = Directory.GetDirectories(_root.Combine("Recovery")).Single();
            Assert.True(File.Exists(Path.Combine(recovery, "session_current.json")));
            Assert.True(File.Exists(Path.Combine(recovery, "Achievements", "cache.json")));
        }
    }
}
