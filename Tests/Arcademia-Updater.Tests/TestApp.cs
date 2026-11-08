using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ArcademiaUpdater.Tests.TestApp))]

namespace ArcademiaUpdater.Tests
{
    public static class TestApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder
                .Configure<App>()
                .UseSkia()
                .UseHarfBuzz()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
