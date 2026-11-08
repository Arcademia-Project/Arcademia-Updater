# Arcademia-Updater
 An Avalonia app for the Arcademia arcade machines, running on Windows and Linux.
## Setup
  Edit the Config.json file to your liking, the template file looks like this:
  ```
  {
    "ApiHost": "https://example.com",
    "ApiUser": "ExampleUsername",
    "ApiPass": "ExamplePassword",
    "NoInputTimeout_ms": 120000,
  }
  ```
  If you leave it this way, it will probably crash, I didn't test it because I'm a professional.
### [Latest Release](https://github.com/Arcademia-Project/Arcademia-Updater/releases/latest "GitHub.com")
## Information
  This is the Updater for [Arcademia-Launcher](https://github.com/Arcademia-Project/Arcademia-Launcher "GitHub.com").
  Refer to the [Docs](https://github.com/Malphatt/Arcademia-Dev-Guide/wiki) for help.
## Releasing
  The version lives in the `<Version>` property of `Arcademia-Updater.csproj` (for example `2.10.2-26`) and is shown on screen as `[v2.10.2-26 Updater Release Build]`.
  1. Bump `<Version>` and commit.
  2. Push a matching tag, e.g. `v2.10.2-26`. The Release workflow checks the tag matches the project version, runs the tests on Windows and Linux and builds:
     - `Arcademia-Updater-<version>-win-x64.zip` (framework-dependent, needs the .NET 10 runtime)
     - `Arcademia-Updater-<version>-linux-x64.zip` (self-contained)
  3. Review the draft release on GitHub, publish it, then upload each zip in the manager using the number without the suffix (e.g. `2.10.2`).
## Linux
  Linux cabinets run an X11 session (not Wayland). Install `libx11-6`, `libxtst6`, `libice6`, `libsm6`, `libfontconfig1` and `fonts-dejavu-core`; SDL2 is bundled.
  The updater asks the launcher to close with `SIGTERM` and watches `.arcademia-launcher.heartbeat` in the install folder to know the launcher is alive.
