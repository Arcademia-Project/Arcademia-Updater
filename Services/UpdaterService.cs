using Microsoft.Extensions.Logging;
using Research_Arcade_Updater.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Research_Arcade_Updater.Services
{
    public enum RepairLevel
    {
        None,
        Reinstall,
        ReinstallAndResetState,
    }

    public interface IUpdaterService
    {
        event EventHandler<LauncherStateChangedEventArgs> StateChanged;

        string LauncherExePath { get; }
        Task<bool> EnsureLauncherReadyAsync(RepairLevel repair, CancellationToken cancellationToken);
        Task<bool> IsUpdateAvailableAsync(CancellationToken cancellationToken);
        void StopLaunchers();
    }

    public class LauncherStateChangedEventArgs(UpdaterState newState) : EventArgs
    {
        public UpdaterState NewState { get; } = newState;
    }

    public class UpdaterService(
        IApiClient apiClient,
        LauncherInstaller installer,
        ILogger<UpdaterService> logger
    ) : IUpdaterService
    {
        public event EventHandler<LauncherStateChangedEventArgs> StateChanged;
        protected void OnStateChanged(UpdaterState s) =>
            StateChanged?.Invoke(this, new LauncherStateChangedEventArgs(s));

        private readonly IApiClient _apiClient = apiClient;
        private readonly LauncherInstaller _installer = installer;
        private readonly ILogger<UpdaterService> _logger = logger;

        public string LauncherExePath => _installer.LauncherExePath;

        public void StopLaunchers() => _installer.StopLaunchers(TimeSpan.FromSeconds(10));

        public async Task<bool> IsUpdateAvailableAsync(CancellationToken cancellationToken)
        {
            var latest = await _apiClient.GetLatestLauncherVersionAsync(_logger, cancellationToken);
            if (latest.Status != LatestLauncherStatus.UpdateAvailable)
                return false;

            var manifest = _installer.ReadManifest();
            return manifest?.Version != latest.VersionNumber;
        }

        public async Task<bool> EnsureLauncherReadyAsync(RepairLevel repair, CancellationToken cancellationToken)
        {
            OnStateChanged(UpdaterState.checkingForUpdates);

            var health = _installer.CheckHealth(out var reason);
            var manifest = _installer.ReadManifest();
            _logger.LogInformation(
                "[UpdaterService] Launcher install: {Health} (version {Version}){Reason}",
                health,
                manifest?.Version ?? "unknown",
                reason == null ? "" : $" - {reason}"
            );

            var latest = await _apiClient.GetLatestLauncherVersionAsync(_logger, cancellationToken);
            bool online = latest.Status != LatestLauncherStatus.Unreachable;

            string targetVersion = latest.Status == LatestLauncherStatus.UpdateAvailable
                ? latest.VersionNumber
                : manifest?.Version;

            bool mustInstall = repair != RepairLevel.None
                || health is InstallHealth.Missing or InstallHealth.Corrupt
                || (health == InstallHealth.Legacy && online)
                || (latest.Status == LatestLauncherStatus.UpdateAvailable && manifest?.Version != latest.VersionNumber);

            if (repair == RepairLevel.ReinstallAndResetState)
            {
                _installer.StopLaunchers(TimeSpan.FromSeconds(10));
                _installer.QuarantineState();
            }

            if (!mustInstall)
            {
                if (latest.Status == LatestLauncherStatus.UpdateAvailable)
                    await ReportVersionAsync(latest.VersionNumber);

                return true;
            }

            OnStateChanged(health == InstallHealth.Verified || health == InstallHealth.Legacy
                ? UpdaterState.updatingLauncher
                : UpdaterState.repairingLauncher);

            if (targetVersion == null && latest.Status == LatestLauncherStatus.UpToDate)
                targetVersion = await ResolveCurrentVersionAsync(cancellationToken);

            string package = _installer.FindValidCachedPackage(targetVersion);

            if (package == null && online && targetVersion != null)
                package = await DownloadAsync(targetVersion, cancellationToken);

            if (package == null)
            {
                var cached = _installer.FindNewestValidCachedPackage();
                if (cached.Path != null)
                {
                    _logger.LogWarning("[UpdaterService] Falling back to cached launcher package {Version}", cached.Version);
                    package = cached.Path;
                    targetVersion = cached.Version;
                }
            }

            if (package == null)
            {
                _logger.LogWarning("[UpdaterService] No launcher package is available to install.");
                if (!online)
                    OnStateChanged(UpdaterState.waitingOnInternet);
                return health != InstallHealth.Missing;
            }

            try
            {
                _installer.Install(package, targetVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UpdaterService] Failed to install launcher {Version}", targetVersion);
                OnStateChanged(UpdaterState.failed);
                return false;
            }

            if (online)
                await ReportVersionAsync(targetVersion);

            return true;
        }

        private async Task<string> ResolveCurrentVersionAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[UpdaterService] Resetting the recorded launcher version to find the current release...");
            try
            {
                await _apiClient.UpdateRemoteLauncherVersionAsync("0.0.0", _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[UpdaterService] Could not reset the recorded launcher version: {Message}", ex.Message);
                return null;
            }

            var latest = await _apiClient.GetLatestLauncherVersionAsync(_logger, cancellationToken);
            return latest.Status == LatestLauncherStatus.UpdateAvailable ? latest.VersionNumber : null;
        }

        private async Task<string> DownloadAsync(string version, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[UpdaterService] Downloading launcher {Version}", version);

            var downloadPath = _installer.PrepareDownloadPath(version);
            try
            {
                await using (var zipStream = await _apiClient.GetLauncherDownloadAsync(version, cancellationToken))
                await using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await zipStream.CopyToAsync(fileStream, cancellationToken);

                return _installer.AcceptDownload(downloadPath, version);
            }
            catch (Exception ex)
            {
                _logger.LogError("[UpdaterService] Download of launcher {Version} failed: {Message}", version, ex.Message);
                try
                {
                    File.Delete(downloadPath);
                }
                catch { }
                return null;
            }
        }

        private async Task ReportVersionAsync(string version)
        {
            try
            {
                if (await _apiClient.UpdateRemoteLauncherVersionAsync(version, _logger))
                    _logger.LogInformation("[UpdaterService] Reported launcher version {Version}", version);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[UpdaterService] Could not report launcher version {Version}: {Message}", version, ex.Message);
            }
        }
    }
}
