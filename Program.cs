using System;
using Avalonia;

namespace ArcademiaUpdater
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args) =>
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
    }
}
