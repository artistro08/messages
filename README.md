<p align="center">
    <img src="src/GMessages.App/Assets/Square44x44Logo.targetsize-256.png" width="128" height="128" alt="Kaituro icon">
</p>

<h1 align="center">
    Kaituro
</h1>

<p align="center">
    Google Messages for Windows: text, share photos and reply to notifications from your PC, right from the tray.
</p>

![Kaituro showing a chat with a photo, reactions and a reply, in dark mode](docs/screenshots/01-chat-dark.png)

## Features

- **Pair with your Google account.** Sign in, match the emoji on your phone, and your chats show up.
- **Lives in the tray.** Close the window and Kaituro keeps running, so new messages still come in. Turn on Start with Windows and it's there when you sign in.
- **Notifications you can answer.** Reply right from the notification, snooze it, or mute a chat you don't want to hear from.
- **RCS and SMS chats.** One-on-one and group chats, with a lock on end-to-end encrypted ones and read receipts on what you send.
- **Photos, videos and files.** Send and receive them, play videos right in the chat, and open photos full screen to zoom in.
- **Voice messages.** Record one with your microphone of choice and play the ones you get.
- **Reactions and replies.** React to any message, reply to a specific one, and jump back to what it quoted.
- **Link previews.** Links show a preview card, and you can turn them off for links you receive.
- **Keep your inbox tidy.** Archive, mute, block and report spam, or move chats to the trash. Chats pinned on your phone stay at the top, and Ctrl + Z puts back the last chat you archived.
- **Keyboard shortcuts.** Google Messages' own shortcuts, plus Ctrl + S to show or hide the sidebar and Ctrl + , for Settings. Press Ctrl + ? to see them all.
- **Fits in on Windows 11.** Light and dark themes, a window that remembers where you left it, and a layout that works down to a narrow window.

## Building

### Prerequisites

- Windows 11
- [Visual Studio 2026](https://visualstudio.microsoft.com/) with the **.NET desktop development** and **Desktop development with C++** workloads (the C++ tools build the Native AOT release)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/), only if you change the icon

### Run It

Build the app, then run its exe:

```powershell
dotnet build src/GMessages.App -c Debug -p:Platform=x64
.\src\GMessages.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kaituro.exe
```

> A debug build keeps its data in `%LOCALAPPDATA%\Kaituro`, apart from the installed app, so you'll pair it separately.

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

> The UI tests drive a real copy of the app against a fake Google server. Quit the installed Kaituro first, and rebuild `src/GMessages.App` before running them so they don't test an old build.

### Change The Icon Or Screenshots

- `.\scripts\Make-Icons.ps1` redraws every icon size and the tray icons from `assets-src/Kaituro.svg`.
- The store screenshots in `docs/screenshots` come from a mock phone. Retake them with:

```powershell
$env:KAITURO_SCREENSHOTS = 1; dotnet test src/GMessages.App.UITests -p:Platform=x64 --filter StoreScreenshots
```
