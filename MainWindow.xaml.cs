using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Research_Arcade_Updater.Models;
using Research_Arcade_Updater.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Research_Arcade_Updater
{
    public partial class MainWindow : Window
    {
        [DllImport("User32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        private const string CloseRequestEventName = @"Local\Arcademia.Launcher.CloseRequested";

        private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan UnresponsiveLimit = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(15);

        private enum LauncherOutcome
        {
            CleanExit,
            Crashed,
            CrashedAfterHealthyRun,
            Hung,
            StartFailed,
            UpdateRequested,
            Stopped,
        }

        private static IHost _host;
        private readonly IUpdaterService _updater;
        private readonly ILogger<MainWindow> _logger;

        private readonly string rootPath;

        private readonly CancellationTokenSource _shutdown = new();
        private readonly EventWaitHandle _closeRequest;
        private Task _supervisor = Task.CompletedTask;
        private bool _shutdownComplete;

        private UpdaterState _state;
        internal UpdaterState State
        {
            get => _state;
            set
            {
                if (Application.Current != null && Application.Current.Dispatcher != null)
                    try
                    {
                        Application.Current.Dispatcher.Invoke(() => {
                            if (_state == value) return;
                            _state = value;

                            switch (_state)
                            {
                                case UpdaterState.idle:
                                    StatusText.Text = "Awaiting Instructions...";
                                    break;
                                case UpdaterState.startingLauncher:
                                    StatusText.Text = "Starting Launcher...";
                                    break;
                                case UpdaterState.closingLauncher:
                                    StatusText.Text = "Closing Launcher...";
                                    break;
                                case UpdaterState.restartingLauncher:
                                    StatusText.Text = "Restarting Launcher...";
                                    break;
                                case UpdaterState.failed:
                                    StatusText.Text = "Failed (Please contact IT for support)";
                                    break;
                                case UpdaterState.checkingForUpdates:
                                    StatusText.Text = "Checking for updates...";
                                    break;
                                case UpdaterState.updatingLauncher:
                                    StatusText.Text = "Updating Launcher...";
                                    break;
                                case UpdaterState.waitingOnInternet:
                                    StatusText.Text = "Waiting for an internet connection...";
                                    break;
                                case UpdaterState.repairingLauncher:
                                    StatusText.Text = "Repairing Launcher...";
                                    break;
                                case UpdaterState.retryingLauncher:
                                    StatusText.Text = "Launcher stopped unexpectedly, retrying...";
                                    break;
                                default:
                                    break;
                            }

                        });
                    }
                    catch (TaskCanceledException) { }
            }
        }

        public MainWindow()
        {
            Closing += Window_Closing;

            InitializeComponent();

            // Setup Directories
            rootPath = Directory.GetCurrentDirectory();

            string configPath = Path.Combine(rootPath, "Config.json");

            // Load the config file
            if (!File.Exists(configPath))
            {
                State = UpdaterState.failed;
                MessageBox.Show("Config file not found");
                return;
            }

            JObject config = JObject.Parse(File.ReadAllText(configPath));

            // Create the Launcher directory if it does not exist
            Directory.CreateDirectory(Path.Combine(rootPath, "Launcher"));

            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new FileLoggerProvider(Path.Combine(rootPath, "Logs"))))
                .ConfigureServices((context, services) => {
                    var host = config["ApiHost"]?.ToString() ?? "https://localhost:5001";
                    var user = config["ApiUser"]?.ToString() ?? "Research-Arcade-User";
                    var pass = config["ApiPass"]?.ToString() ?? "Research-Arcade-Password";

                    var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

                    services
                        .AddHttpClient<IApiClient, ApiClient>(client =>
                        {
                            client.BaseAddress = new Uri(host);
                            client.Timeout = TimeSpan.FromSeconds(30);
                            client.DefaultRequestHeaders.Authorization =
                                new AuthenticationHeaderValue("ArcadeMachine", creds);
                        });

                    services.AddSingleton<LauncherInstaller>();
                    services.AddSingleton<IUpdaterService, UpdaterService>();
                })
                .Build();

            _logger = _host.Services.GetRequiredService<ILogger<MainWindow>>();
            _updater = _host.Services.GetRequiredService<IUpdaterService>();
            _updater.StateChanged += Updater_StateChanged;

            _closeRequest = new EventWaitHandle(false, EventResetMode.AutoReset, CloseRequestEventName);
            _closeRequest.Reset();

            _logger.LogInformation("[Updater] Started in {Root}", rootPath);

            // Find the Launcher process and close it
            _updater.StopLaunchers();

            _supervisor = Task.Run(() => SuperviseAsync(_shutdown.Token));
        }

        private void Updater_StateChanged(object sender, LauncherStateChangedEventArgs e) => State = e.NewState;

        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_shutdownComplete)
                return;

            e.Cancel = true;
            if (_shutdown.IsCancellationRequested)
                return;

            _logger?.LogInformation("[Updater] Shutting down");
            _shutdown.Cancel();

            // Close the launcher
            await Task.WhenAny(_supervisor, Task.Delay(GracefulCloseTimeout + TimeSpan.FromSeconds(10)));

            // Dispose of the host
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }

            _shutdownComplete = true;
            Application.Current.Shutdown();
        }

        private async Task SuperviseAsync(CancellationToken cancellationToken)
        {
            var repair = RepairLevel.None;
            int failures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                bool ready;
                try
                {
                    ready = await _updater.EnsureLauncherReadyAsync(repair, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Updater] Could not prepare the launcher");
                    ready = false;
                }

                if (!ready)
                {
                    failures++;
                    State = UpdaterState.retryingLauncher;
                    await DelayAsync(Backoff(failures), cancellationToken);
                    continue;
                }

                repair = RepairLevel.None;

                var outcome = await RunLauncherAsync(cancellationToken);
                _logger.LogInformation("[Updater] Launcher finished: {Outcome}", outcome);

                switch (outcome)
                {
                    case LauncherOutcome.Stopped:
                        return;
                    case LauncherOutcome.CleanExit:
                    case LauncherOutcome.UpdateRequested:
                        failures = 0;
                        break;
                    case LauncherOutcome.CrashedAfterHealthyRun:
                        failures = 1;
                        break;
                    default:
                        failures++;
                        break;
                }

                repair = failures switch
                {
                    <= 1 => RepairLevel.None,
                    2 => RepairLevel.Reinstall,
                    _ => RepairLevel.ReinstallAndResetState,
                };

                if (repair != RepairLevel.None)
                    _logger.LogWarning("[Updater] {Failures} consecutive launcher failures, next start will use repair level {Repair}", failures, repair);

                State = failures > 0 ? UpdaterState.retryingLauncher : UpdaterState.restartingLauncher;
                await DelayAsync(failures > 0 ? Backoff(failures) : TimeSpan.FromSeconds(3), cancellationToken);
            }
        }

        private static TimeSpan Backoff(int failures) =>
            failures <= 3
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromSeconds(Math.Min(30 * (failures - 3), 300));

        private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) { }
        }

        private async Task<LauncherOutcome> RunLauncherAsync(CancellationToken cancellationToken)
        {
            var exePath = _updater.LauncherExePath;
            if (!File.Exists(exePath))
                return LauncherOutcome.StartFailed;

            State = UpdaterState.startingLauncher;
            _closeRequest.Reset();

            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(exePath)
                {
                    WorkingDirectory = rootPath,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Updater] Could not start the launcher");
                return LauncherOutcome.StartFailed;
            }

            if (process == null)
                return LauncherOutcome.StartFailed;

            var runTime = Stopwatch.StartNew();
            Stopwatch unresponsiveFor = null;
            bool windowShown = false;
            var nextUpdateCheck = DateTime.UtcNow + UpdateInterval;

            try
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await CloseLauncherAsync(process, graceful: true);
                        return LauncherOutcome.Stopped;
                    }

                    if (process.HasExited)
                    {
                        _logger.LogInformation(
                            "[Updater] Launcher exited with code {ExitCode} after {Seconds:N0}s",
                            process.ExitCode,
                            runTime.Elapsed.TotalSeconds
                        );

                        if (process.ExitCode == 0)
                            return LauncherOutcome.CleanExit;

                        return windowShown && runTime.Elapsed >= HealthyAfter
                            ? LauncherOutcome.CrashedAfterHealthyRun
                            : LauncherOutcome.Crashed;
                    }

                    process.Refresh();

                    if (!windowShown)
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            windowShown = true;
                            _logger.LogInformation("[Updater] Launcher window shown after {Seconds:N1}s", runTime.Elapsed.TotalSeconds);

                            // Bring the launcher to the front
                            SetForegroundWindow(process.MainWindowHandle);
                            State = UpdaterState.idle;
                        }
                        else if (runTime.Elapsed > WindowTimeout)
                        {
                            _logger.LogError("[Updater] Launcher showed no window within {Seconds}s, treating it as hung", WindowTimeout.TotalSeconds);
                            await CloseLauncherAsync(process, graceful: false);
                            return LauncherOutcome.Hung;
                        }
                    }
                    else if (process.Responding)
                    {
                        unresponsiveFor = null;
                    }
                    else
                    {
                        unresponsiveFor ??= Stopwatch.StartNew();
                        if (unresponsiveFor.Elapsed > UnresponsiveLimit)
                        {
                            _logger.LogError("[Updater] Launcher has not responded for {Minutes} minutes, restarting it", UnresponsiveLimit.TotalMinutes);
                            await CloseLauncherAsync(process, graceful: false);
                            return LauncherOutcome.Hung;
                        }
                    }

                    if (DateTime.UtcNow >= nextUpdateCheck)
                    {
                        nextUpdateCheck = DateTime.UtcNow + UpdateInterval;

                        bool updateAvailable = false;
                        try
                        {
                            updateAvailable = await _updater.IsUpdateAvailableAsync(cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning("[Updater] Scheduled update check failed: {Message}", ex.Message);
                        }

                        if (updateAvailable)
                        {
                            _logger.LogInformation("[Updater] A launcher update is available, closing the launcher to install it");
                            await CloseLauncherAsync(process, graceful: true);
                            return LauncherOutcome.UpdateRequested;
                        }
                    }

                    try
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        await CloseLauncherAsync(process, graceful: true);
                        return LauncherOutcome.Stopped;
                    }
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        private async Task CloseLauncherAsync(Process process, bool graceful)
        {
            State = UpdaterState.closingLauncher;

            try
            {
                if (process.HasExited)
                    return;

                if (graceful)
                {
                    _closeRequest.Set();

                    var waited = Stopwatch.StartNew();
                    while (!process.HasExited && waited.Elapsed < GracefulCloseTimeout)
                        await Task.Delay(250);
                }

                if (!process.HasExited)
                {
                    _logger.LogWarning("[Updater] Force-stopping the launcher");
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Updater] Could not close the launcher: {Message}", ex.Message);
            }
            finally
            {
                _closeRequest.Reset();
            }
        }
    }
}
