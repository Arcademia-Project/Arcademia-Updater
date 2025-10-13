using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Research_Arcade_Updater.Models;
using Research_Arcade_Updater.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Research_Arcade_Updater
{
    public partial class MainWindow : Window
    {
        bool failed = false;

        // Send a key press
        [DllImport("User32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("User32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        private static IHost _host;
        private readonly IUpdaterService _updater;

        private readonly string rootPath;
        private readonly string launcherPath;

        private Process launcherProcess = null;

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

            launcherPath = Path.Combine(rootPath, "Launcher");

            // Create the Launcher directory if it does not exist
            if (!Directory.Exists(launcherPath))
                Directory.CreateDirectory(launcherPath);

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) => {
                    var host = config["ApiHost"]?.ToString() ?? "https://localhost:5001";
                    var user = config["ApiUser"]?.ToString() ?? "Research-Arcade-User";
                    var pass = config["ApiPass"]?.ToString() ?? "Research-Arcade-Password";

                    var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

                    services
                        .AddHttpClient<IApiClient, ApiClient>(client =>
                        {
                            client.BaseAddress = new Uri(host);
                            client.DefaultRequestHeaders.Authorization =
                                new AuthenticationHeaderValue("ArcadeMachine", creds);
                        });

                    services.AddSingleton<IUpdaterService, UpdaterService>();
                })
                .Build();

            _updater = _host.Services.GetRequiredService<IUpdaterService>();
            _updater.StateChanged += Updater_StateChanged;
            _updater.OnLauncherRestartRequired += RestartLauncher;

            // Find the Launcher process and close it
            Process[] processes = Process.GetProcessesByName("Research-Arcade-Launcher");
            foreach (Process process in processes)
                process.Kill();

            // Store the start time
            DateTime startTime = DateTime.Now;

            //StartLauncher();

            // Initialize the update timer
            Task.Run(async () =>
            {
                await CheckForUpdates();

                while (true)
                {
                    // Wait 1 hour before checking for updates again
                    await Task.Delay(60 * 60 * 1000);
                    await CheckForUpdates();
                }
            });
        }

        private void Updater_StateChanged(object sender, LauncherStateChangedEventArgs e) => Dispatcher.Invoke(() => State = e.NewState);
        private void RestartLauncher(object sender, EventArgs e) => CloseLauncher();
        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            // Close the launcher
            CloseLauncher();

            // Dispose of the host
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }

        private void Launcher_Closing(object sender, CancelEventArgs e)
        {
            launcherProcess = null;
            State = UpdaterState.restartingLauncher;

            // After 5 seconds start the launcher
            Task.Run(async () =>
            {
                await Task.Delay(5000);
                await CheckForUpdates();
            });
        }

        private void StartLauncher()
        {
            if (launcherProcess == null)
            {
                State = UpdaterState.startingLauncher;

                // Check if the file exists
                if (!File.Exists(Path.Combine(launcherPath, "Research-Arcade-Launcher.exe")))
                {
                    _updater.ResetRemoteMachineLauncherVersionAsync(CancellationToken.None).ContinueWith((_) => CheckForUpdates());
                    return;
                }

                launcherProcess = Process.Start(Path.Combine(launcherPath, "Research-Arcade-Launcher.exe"));

                if (failed)
                    Task.Delay(1000).ContinueWith((_) => State = UpdaterState.failed);

                // Check if the launcher process has quit
                Task.Run(async () =>
                {
                    while (launcherProcess != null && !launcherProcess.HasExited)
                        await Task.Delay(1000);

                    Launcher_Closing(null, null);
                });

                // Bring the launcher to the front
                SetForegroundWindow(launcherProcess.MainWindowHandle);
            }
        }

        private void CloseLauncher()
        {
            if (launcherProcess != null)
            {
                State = UpdaterState.closingLauncher;

                // Send the close key to the launcher
                keybd_event(69, 0, 0, 0);

                // Wait for the launcher to close
                launcherProcess?.WaitForExit();
                launcherProcess = null;

                // Release the close key
                keybd_event(69, 0, 2, 0);
            }
        }

        private async Task CheckForUpdates()
        {
            try
            {
                await _updater.CheckAndUpdateAsync(CancellationToken.None);

                // Start the launcher
                StartLauncher();

                // After 3 seconds set the state to idle
                await Task.Delay(3000);
                State = UpdaterState.idle;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking for updates: {ex.Message}");

                State = UpdaterState.failed;
                failed = true;

                // If the application isn't open, open it after 5 seconds
                await Task.Delay(5000);
                StartLauncher();
            }
        }
    }
}