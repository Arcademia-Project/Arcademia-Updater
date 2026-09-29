using Microsoft.Extensions.Logging;
using Research_Arcade_Updater.Models;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Research_Arcade_Updater.Services
{
    public enum LatestLauncherStatus
    {
        UpdateAvailable,
        UpToDate,
        Unreachable,
    }

    public sealed record LatestLauncherResult(LatestLauncherStatus Status, string VersionNumber);

    public interface IApiClient
    {
        Task<LatestLauncherResult> GetLatestLauncherVersionAsync(
            string installedVersion,
            ILogger<UpdaterService> _logger,
            CancellationToken cancellationToken
        );
        Task<Stream> GetLauncherDownloadAsync(
            string versionNumber,
            CancellationToken cancellationToken
        );
        Task<bool> UpdateRemoteLauncherVersionAsync(
            string newVersion,
            ILogger<UpdaterService> _logger
        );
    }
    public class ApiClient(HttpClient http) : IApiClient
    {
        private readonly HttpClient _http = http;

        public async Task<LatestLauncherResult> GetLatestLauncherVersionAsync(
            string installedVersion,
            ILogger<UpdaterService> _logger,
            CancellationToken cancellationToken
        )
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(
                    $"/api/LauncherVersions/Latest?currentVersion={Uri.EscapeDataString(installedVersion ?? "0.0.0")}",
                    cancellationToken
                );
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("[ApiClient] Could not reach the server: {message}", ex.Message);
                return new LatestLauncherResult(LatestLauncherStatus.Unreachable, null);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                    return new LatestLauncherResult(LatestLauncherStatus.UpdateAvailable, body.Trim().Trim('"'));

                if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("No update needed", StringComparison.OrdinalIgnoreCase))
                    return new LatestLauncherResult(LatestLauncherStatus.UpToDate, null);

                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(
                        "[ApiClient] Unexpected response from GetLatestLauncherVersionAsync: {StatusCode} {message}",
                        response.StatusCode,
                        body
                    );
                return new LatestLauncherResult(LatestLauncherStatus.Unreachable, null);
            }
        }

        public async Task<Stream> GetLauncherDownloadAsync(
            string versionNumber,
            CancellationToken cancellationToken
        )
        {
            var response = await _http.GetAsync(
                $"/api/LauncherVersions/Download?versionNumber={versionNumber}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"Failed to download launcher: {response.StatusCode} - {error}");
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }

        public async Task<bool> UpdateRemoteLauncherVersionAsync(string newVersion, ILogger<UpdaterService> _logger)
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { VersionNumber = newVersion }),
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var response = await _http.PutAsync("/api/LauncherVersions/UpdateVersion", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("[ApiClient] Bad Request: {message}", errorMessage);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("[ApiClient] Not Found: {message}", errorMessage);
                }
                else if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("[ApiClient] Unexpected error whilst executing UpdateRemoteLauncherVersionAsync: {StatusCode}", response.StatusCode);
            }
            response.EnsureSuccessStatusCode();

            return response.IsSuccessStatusCode;
        }
    }
}
