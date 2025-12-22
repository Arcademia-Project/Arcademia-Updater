using Microsoft.Extensions.Logging;
using Research_Arcade_Updater.Models;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Research_Arcade_Updater.Services
{
    public interface IApiClient
    {
        Task<string> GetLatestLauncherVersionAsync(ILogger<UpdaterService> _logger);
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

        public async Task<string> GetLatestLauncherVersionAsync(ILogger<UpdaterService> _logger)
        {
            var response = await _http.GetAsync("/api/LauncherVersions/Latest");

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("[ApiClient] Warning whilst executing GetLatestLauncherVersionAsync: {message}", errorMessage);
                }

                else if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError("[ApiClient] Unexpected error whilst executing GetLatestLauncherVersionAsync: {StatusCode}", response.StatusCode);

                throw new InvalidOperationException("Failed to retrieve LauncherInfo.");
            }
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }

        public async Task<Stream> GetLauncherDownloadAsync(
            string versionNumber,
            CancellationToken cancellationToken
        )
        {
            var response = await _http.GetAsync(
                $"/api/LauncherVersions/Download?versionNumber={versionNumber}",
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
