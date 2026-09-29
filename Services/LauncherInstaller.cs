using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Research_Arcade_Updater.Services
{
    public sealed class InstallManifest
    {
        public string Version { get; set; }
        public DateTime InstalledAtUtc { get; set; }
        public List<InstallManifestFile> Files { get; set; } = [];
    }

    public sealed class InstallManifestFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    public enum InstallHealth
    {
        Verified,
        Legacy,
        Missing,
        Corrupt,
    }

    public sealed class LauncherInstaller
    {
        public const string LauncherExeName = "Research-Arcade-Launcher.exe";
        public const string LauncherProcessName = "Research-Arcade-Launcher";

        private const string ManifestName = ".arcademia-install.json";
        private const string PackagePrefix = "launcher-";

        private static readonly string[] LegacyBinaryPatterns =
        [
            "*.dll",
            "*.exe",
            "*.pdb",
            "*.deps.json",
            "*.runtimeconfig.json",
            "*.dll.config",
        ];

        private static readonly string[] StateFiles = ["session_current.json", "session_queue.json"];
        private static readonly string[] StateDirectories = ["Achievements"];
        private static readonly string[] ProtectedDirectories = ["Games", "Achievements", "Logs", "Recovery"];
        private static readonly string[] ProtectedFiles =
        [
            "session_current.json",
            "session_queue.json",
            "ControllerMapping.json",
            "Config.json",
            ManifestName,
        ];

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly ILogger<LauncherInstaller> _logger;
        private readonly string _rootDir;
        private readonly string _launcherDir;
        private readonly string _stagingDir;
        private readonly string _packagesDir;
        private readonly string _recoveryDir;

        public LauncherInstaller(ILogger<LauncherInstaller> logger)
        {
            _logger = logger;
            _rootDir = Directory.GetCurrentDirectory();
            _launcherDir = Path.Combine(_rootDir, "Launcher");
            _stagingDir = Path.Combine(_rootDir, "Launcher.staging");
            _packagesDir = Path.Combine(_rootDir, "Packages");
            _recoveryDir = Path.Combine(_rootDir, "Recovery");
        }

        public string LauncherDirectory => _launcherDir;
        public string LauncherExePath => Path.Combine(_launcherDir, LauncherExeName);
        private string ManifestPath => Path.Combine(_launcherDir, ManifestName);

        public InstallManifest ReadManifest()
        {
            try
            {
                if (!File.Exists(ManifestPath))
                    return null;
                return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(ManifestPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Installer] Could not read the install manifest: {Message}", ex.Message);
                return null;
            }
        }

        public InstallHealth CheckHealth(out string reason)
        {
            var manifest = ReadManifest();

            if (manifest == null)
            {
                if (File.Exists(LauncherExePath))
                {
                    reason = "No install manifest (installed by an older updater).";
                    return InstallHealth.Legacy;
                }

                reason = "The launcher is not installed.";
                return InstallHealth.Missing;
            }

            if (manifest.Files.Count == 0)
            {
                reason = "The install manifest is empty.";
                return InstallHealth.Corrupt;
            }

            foreach (var file in manifest.Files)
            {
                var path = SafeCombine(_launcherDir, file.Path);
                if (path == null || !File.Exists(path))
                {
                    reason = $"Missing file: {file.Path}";
                    return InstallHealth.Corrupt;
                }

                var info = new FileInfo(path);
                if (info.Length != file.Size)
                {
                    reason = $"Size mismatch: {file.Path}";
                    return InstallHealth.Corrupt;
                }

                if (!string.Equals(HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"Content mismatch: {file.Path}";
                    return InstallHealth.Corrupt;
                }
            }

            reason = null;
            return InstallHealth.Verified;
        }

        public string GetPackagePath(string version) =>
            Path.Combine(_packagesDir, $"{PackagePrefix}{version}.zip");

        public string FindValidCachedPackage(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return null;

            var path = GetPackagePath(version);
            return File.Exists(path) && IsValidPackage(path) ? path : null;
        }

        public (string Path, string Version) FindNewestValidCachedPackage()
        {
            if (!Directory.Exists(_packagesDir))
                return (null, null);

            var candidates = Directory
                .GetFiles(_packagesDir, $"{PackagePrefix}*.zip")
                .Select(path => (Path: path, Version: Path.GetFileNameWithoutExtension(path)[PackagePrefix.Length..]))
                .OrderByDescending(c => new Classes.Version(c.Version))
                .ToList();

            foreach (var candidate in candidates)
                if (IsValidPackage(candidate.Path))
                    return candidate;

            return (null, null);
        }

        public string PrepareDownloadPath(string version)
        {
            Directory.CreateDirectory(_packagesDir);
            return GetPackagePath(version) + ".part";
        }

        public string AcceptDownload(string downloadPath, string version)
        {
            ValidatePackage(downloadPath);

            var finalPath = GetPackagePath(version);
            File.Move(downloadPath, finalPath, true);

            foreach (var other in Directory.GetFiles(_packagesDir, $"{PackagePrefix}*"))
                if (!string.Equals(other, finalPath, StringComparison.OrdinalIgnoreCase))
                    TryDeleteFile(other);

            return finalPath;
        }

        public bool IsValidPackage(string zipPath)
        {
            try
            {
                ValidatePackage(zipPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Installer] Package {Package} is not usable: {Message}", Path.GetFileName(zipPath), ex.Message);
                return false;
            }
        }

        public void ValidatePackage(string zipPath)
        {
            using var zip = new ZipFile(zipPath);

            if (!zip.TestArchive(true))
                throw new InvalidDataException("The package failed its integrity check.");

            bool hasLauncher = false;
            foreach (ZipEntry entry in zip)
            {
                var name = entry.Name.Replace('\\', '/');
                if (Path.IsPathRooted(name) || name.Split('/').Contains(".."))
                    throw new InvalidDataException($"The package contains an unsafe path: {entry.Name}");

                if (string.Equals(name, LauncherExeName, StringComparison.OrdinalIgnoreCase))
                    hasLauncher = true;
            }

            if (!hasLauncher)
                throw new InvalidDataException($"The package does not contain {LauncherExeName}.");
        }

        public void Install(string zipPath, string version)
        {
            _logger.LogInformation("[Installer] Installing launcher {Version} from {Package}", version, Path.GetFileName(zipPath));

            ValidatePackage(zipPath);

            TryDeleteDirectory(_stagingDir);
            Directory.CreateDirectory(_stagingDir);
            new FastZip().ExtractZip(zipPath, _stagingDir, null);

            var stagedFiles = Directory
                .GetFiles(_stagingDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_stagingDir, f))
                .Where(f =>
                {
                    if (!IsProtected(f))
                        return true;

                    _logger.LogWarning("[Installer] Skipping {File} from the package; it would overwrite machine data", f);
                    return false;
                })
                .ToList();

            if (!stagedFiles.Any(f => string.Equals(f, LauncherExeName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"The extracted package does not contain {LauncherExeName}.");

            var manifest = new InstallManifest
            {
                Version = version,
                InstalledAtUtc = DateTime.UtcNow,
                Files = stagedFiles
                    .Select(relativePath =>
                    {
                        var stagedPath = Path.Combine(_stagingDir, relativePath);
                        return new InstallManifestFile
                        {
                            Path = relativePath,
                            Size = new FileInfo(stagedPath).Length,
                            Sha256 = HashFile(stagedPath),
                        };
                    })
                    .ToList(),
            };

            StopLaunchers(TimeSpan.FromSeconds(10));

            var previous = ReadManifest();
            Directory.CreateDirectory(_launcherDir);
            if (File.Exists(ManifestPath))
                DeleteWithRetry(ManifestPath);

            var toRemove = new HashSet<string>(stagedFiles, StringComparer.OrdinalIgnoreCase);
            if (previous != null)
            {
                foreach (var file in previous.Files)
                    toRemove.Add(file.Path);
            }
            else
            {
                foreach (var pattern in LegacyBinaryPatterns)
                    foreach (var file in Directory.GetFiles(_launcherDir, pattern))
                        toRemove.Add(Path.GetFileName(file));
            }

            foreach (var relativePath in toRemove)
            {
                if (IsProtected(relativePath))
                    continue;

                var target = SafeCombine(_launcherDir, relativePath);
                if (target != null && File.Exists(target))
                    DeleteWithRetry(target);
            }

            foreach (var relativePath in stagedFiles)
            {
                var target = Path.Combine(_launcherDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(_stagingDir, relativePath), target, true);
            }

            var manifestTemp = ManifestPath + ".tmp";
            File.WriteAllText(manifestTemp, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(manifestTemp, ManifestPath, true);

            TryDeleteDirectory(_stagingDir);

            _logger.LogInformation("[Installer] Launcher {Version} installed and verified ({Count} files)", version, manifest.Files.Count);
        }

        public void QuarantineState()
        {
            var destination = Path.Combine(_recoveryDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            bool movedAnything = false;

            foreach (var name in StateFiles)
            {
                var source = Path.Combine(_launcherDir, name);
                if (!File.Exists(source))
                    continue;

                try
                {
                    Directory.CreateDirectory(destination);
                    File.Move(source, Path.Combine(destination, name), true);
                    movedAnything = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Installer] Could not move {File} aside: {Message}", name, ex.Message);
                    TryDeleteFile(source);
                }
            }

            foreach (var name in StateDirectories)
            {
                var source = Path.Combine(_launcherDir, name);
                if (!Directory.Exists(source))
                    continue;

                try
                {
                    Directory.CreateDirectory(destination);
                    Directory.Move(source, Path.Combine(destination, name));
                    movedAnything = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Installer] Could not move {Directory} aside: {Message}", name, ex.Message);
                }
            }

            if (movedAnything)
                _logger.LogWarning("[Installer] Moved launcher state to {Destination}", destination);
        }

        public void StopLaunchers(TimeSpan timeout)
        {
            foreach (var process in Process.GetProcessesByName(LauncherProcessName))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        _logger.LogWarning("[Installer] Stopping launcher process {Id}", process.Id);
                        process.Kill(true);
                        process.WaitForExit((int)timeout.TotalMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[Installer] Could not stop launcher process: {Message}", ex.Message);
                    }
                }
            }
        }

        private static bool IsProtected(string relativePath)
        {
            var parts = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return true;

            if (parts.Length > 1)
                return ProtectedDirectories.Contains(parts[0], StringComparer.OrdinalIgnoreCase);

            return ProtectedFiles.Contains(parts[0], StringComparer.OrdinalIgnoreCase);
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static string SafeCombine(string root, string relativePath)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private static void DeleteWithRetry(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return;
                }
                catch (Exception) when (attempt < 10)
                {
                    Thread.Sleep(300);
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
