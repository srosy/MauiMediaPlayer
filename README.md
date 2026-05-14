# Mosaic

**Mosaic** is a cross-platform local media player and slideshow built with [.NET MAUI](https://learn.microsoft.com/dotnet/maui/) and **Blazor Hybrid**. It plays images, animated GIFs, and video with native in-app rendering via [CommunityToolkit.Maui.MediaElement](https://github.com/CommunityToolkit/Maui).

| | |
| --- | --- |
| **Solution** | `MauiMediaPlayer.sln` |
| **UI** | Blazor (`wwwroot`, `Components`) |
| **Targets** | Windows, Android (iOS / Mac Catalyst when built on macOS) |

## Features

- Playlist with shuffle, loop modes, and per-layout playback
- Single, bi-split, and tri-split layouts with optional “always show a video” rules
- Native video layer with smooth transitions, prefetch, and image holdover between items
- Dark-first UI with transport controls, toasts, settings popover, and keyboard shortcut help (where supported)
- Settings persisted locally (playback speed, image duration, volume, layout, and more)

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [.NET MAUI workload](https://learn.microsoft.com/dotnet/maui/get-started/installation) (`dotnet workload install maui`)

Optional:

- **Windows**: Windows App SDK / WinUI prerequisites for the Windows target
- **Android**: Android SDK (installed with the MAUI workload) and a device or emulator for `net10.0-android36.0`

## Build and run

The Android target uses **`net10.0-android36.0`** (API level in the TFM). A bare `net10.0-android` moniker causes NuGet **[NU1012](https://learn.microsoft.com/nuget/reference/errors-and-warnings/nu1012)** (“platform version is not present”) with current .NET 10 tooling.

From the repository root (this folder — the same directory as `MauiMediaPlayer.sln`):

```bash
dotnet workload restore
dotnet build MauiMediaPlayer.sln
```

Because the project multi-targets **Android** and **Windows**, `dotnet build MauiMediaPlayer.sln` (and a plain `dotnet restore` on the solution) expects the **Android** MAUI workload to be installed as well. If you see `NETSDK1147` mentioning `android`, install the full MAUI workload (`dotnet workload install maui`) or use the Windows-only commands below.

If you only have the **Windows** MAUI workload installed and do not want the Android SDK pulled in yet, you can scope restore and build to the Windows target (this overrides the multi-target project for that invocation):

```bash
dotnet restore MauiMediaPlayer.csproj -p:TargetFrameworks=net10.0-windows10.0.19041.0
dotnet build MauiMediaPlayer.csproj -c Release -p:TargetFrameworks=net10.0-windows10.0.19041.0
```

**Windows (debug, launches the app):**

```bash
dotnet build MauiMediaPlayer.csproj -t:Run -f net10.0-windows10.0.19041.0 -c Debug
```

**Android (USB device or emulator):**

```bash
dotnet build MauiMediaPlayer.csproj -t:Run -f net10.0-android36.0 -c Debug
```

**Release APK (sideload):**

```bash
dotnet publish MauiMediaPlayer.csproj -f net10.0-android36.0 -c Release -p:AndroidPackageFormat=apk
```

## Platform notes

| Platform | Notes |
| --- | --- |
| **Windows** | Folder picker, fullscreen helpers, and “reveal in file manager” use WinUI implementations under `Platforms/Windows`. |
| **Android / other** | Folder picking and some desktop-only integrations use no-op or limited implementations; use **Add files** / the system picker. Drag-and-drop is oriented toward desktop. |
| **iOS / Mac Catalyst** | Included in the project file when building on macOS; not validated in this repo’s default CI (Windows build only). |

## Configuration

- App name, tagline, version string, and accent color: `Branding.cs`
- Package / display name / app id: `MauiMediaPlayer.csproj` (`ApplicationTitle`, `ApplicationId`, versions)

Before publishing, set a unique **`ApplicationId`** (for example `com.yourname.mosaic`).

## Third-party notices

This project depends on open-source components including .NET MAUI, ASP.NET Core Blazor WebView, CommunityToolkit.Maui (MediaElement), and Bootstrap (see `wwwroot` / UI assets). See also `Branding.Credits` in code for a short summary.

## License

This repository is released under the [MIT License](LICENSE).

## Contributing

Issues and pull requests are welcome. Please keep changes focused and match existing style in the touched files.

For security-sensitive reports, see [SECURITY.md](SECURITY.md).

## Publishing to GitHub

Use this folder (the **repository root** — the directory that contains `MauiMediaPlayer.sln`) as what you push to GitHub. After [creating an empty repository](https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository) on GitHub:

```bash
cd /path/to/MauiMediaPlayer
git remote add origin https://github.com/YOUR_USER/YOUR_REPO.git
git commit -m "Initial import"
git push -u origin main
```

If `git commit` fails because of a local hook or alias (for example an unsupported `trailer` option), fix or bypass your Git configuration for this repo, then commit again.
