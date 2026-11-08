using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace ArcademiaUpdater.Tests
{
    internal sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arcademia-tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch { }
        }
    }

    internal static class Packages
    {
        public static string Create(string zipPath, IDictionary<string, string> files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            using var stream = File.Create(zipPath);
            using var zip = new ZipOutputStream(stream);
            foreach (var (name, content) in files)
            {
                zip.PutNextEntry(new ZipEntry(name));
                var bytes = Encoding.UTF8.GetBytes(content);
                zip.Write(bytes, 0, bytes.Length);
                zip.CloseEntry();
            }
            return zipPath;
        }

        public static Dictionary<string, string> Launcher(string version, params (string Name, string Content)[] extra)
        {
            var files = new Dictionary<string, string>
            {
                [Services.LauncherInstaller.LauncherExeName] = $"launcher {version}",
                ["Arcademia-Launcher.dll"] = $"assembly {version}",
            };
            foreach (var (name, content) in extra)
                files[name] = content;
            return files;
        }
    }
}
