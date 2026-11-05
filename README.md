<p align="center">
    <img src="src/GMessages.App/Assets/Square44x44Logo.targetsize-256.png" width="128" height="128" alt="Messages icon">
</p>

<h1 align="center">
    Messages
</h1>

<p align="center">
   Google messages but Native. 
</p>

![Messages showing a chat with a photo, reactions and a reply, in dark mode](docs/screenshots/01-chat-dark.png)

## Why? 
I'm literally sick and tired of web apps. They are bloated. they take up memory. they literally run a web browser. The idea here was to create something that was light on memory when using it and have an app that doesn't take up much resources when running in the background.

## Features

- Everything Google Messages for Web does
- Runs in the background
- Native Notifications
- Undo Archived messages you've accidentally archived
- Shortcuts, Press Ctrl + ? to see them all.
- Native, Uses WinUI 2.8 With Smooth Window Resizing.
- Looks beautiful. 

## Building

### Prerequisites

- Windows 11
- [Visual Studio 2026](https://visualstudio.microsoft.com/) with the **.NET desktop development** and **Desktop development with C++** workloads (the C++ tools build the Native AOT release), plus **WinUI application development** with its **Universal Windows Platform tools** option (they compile the app's XAML)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/), only if you change the icon

### Run It

Build the app from a **Developer PowerShell for VS 2026** (its XAML only compiles with Visual Studio's MSBuild, not `dotnet build`), then run its exe:

```powershell
msbuild src/GMessages.App -restore -p:Configuration=Debug -p:Platform=x64
.\src\GMessages.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Messages.exe
```

> A debug build keeps its data in `%LOCALAPPDATA%\Messages`, apart from the installed app, so you'll pair it separately.

### Build The Installer

Releases are signed MSIX packages for x64 and ARM64. The first time, create the signing certificate and trust it on your PC from an elevated PowerShell:

```powershell
.\scripts\New-DevCertificate.ps1
```

Then build the packages:

```powershell
.\scripts\Build-Package.ps1
```

They land in `src\GMessages.App\AppPackages`. Open the `.msix` for your PC to install it.

> The certificate's subject has to match `Publisher` in `src/GMessages.App/Package.appxmanifest`. If you're building your own copy, change both to your own and pass it with `-Subject` and `-CertificateSubject`.

### Run The Tests

```powershell
dotnet test src/GMessages.Presentation.Tests
dotnet test src/GMessages.App.UITests -p:Platform=x64
```

> The UI tests drive a real copy of the app against a fake Google server. Quit the installed Messages first, and rebuild `src/GMessages.App` before running them so they don't test an old build.

### Change The Icon Or Screenshots

- `.\scripts\Make-Icons.ps1` redraws every icon size and the tray icons from `assets-src/Messages.svg`.
- The store screenshots in `docs/screenshots` come from a mock phone. Retake them with:

```powershell
$env:MESSAGES_SCREENSHOTS = 1; dotnet test src/GMessages.App.UITests -p:Platform=x64 --filter StoreScreenshots
```
