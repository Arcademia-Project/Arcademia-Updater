using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ArcademiaUpdater.Tests
{
    public class WindowTests
    {
        [AvaloniaFact]
        public void Missing_config_is_reported_on_screen()
        {
            using var root = new TempDirectory();
            var previous = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(root.Path);
            try
            {
                var window = new MainWindow();
                window.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.Equal("Config file not found", window.FindControl<TextBlock>("StatusText").Text);
                Assert.Equal(AppInfo.VersionLabel, window.FindControl<TextBlock>("VersionText").Text);

                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                var renders = Path.Combine(AppContext.BaseDirectory, "renders");
                Directory.CreateDirectory(renders);
                frame.Save(Path.Combine(renders, "updater.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                window.Close();
            }
            finally
            {
                Directory.SetCurrentDirectory(previous);
            }
        }
    }
}
