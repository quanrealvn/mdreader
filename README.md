# MdReader

A small Markdown reader for Windows. Double-click a `.md` file and read it with proper formatting, code highlighting, diagrams and math, in light or dark mode.

There's also a web version at <https://mdreader.fly.dev>. Paste some Markdown or open a few files, and read them in your browser.

![MdReader](.github/screenshot.png)

## Download

- **Windows:** [MdReader-Setup.exe](https://github.com/quanrealvn/mdreader/releases/latest/download/MdReader-Setup.exe). Works on Windows 10 and 11. The installer isn't signed yet, so Windows may show a SmartScreen warning. Click "More info", then "Run anyway".
- **VS Code:** [mdreader-vscode.vsix](https://github.com/quanrealvn/mdreader/releases/latest/download/mdreader-vscode.vsix) adds an "Open in MdReader" command. In VS Code, go to Extensions, open the "…" menu and pick "Install from VSIX".

To make MdReader your default for `.md` files, right-click one, choose **Open with** → **Choose another app**, pick MdReader and tick **Always**.

## What it does

- GitHub-flavored Markdown, including alerts, tables, footnotes and task lists
- Code highlighting, Mermaid diagrams and KaTeX math
- Tabs that come back the next time you open it, and files that reload by themselves when you save them
- Tick task-list checkboxes while reading, and they are saved back to the file
- Side by side: type on the left, see it rendered on the right
- Contents panel you can resize, filter and sort, find, zoom, print and PDF export
- Light or dark theme

## Building it

You need Windows, the .NET 10 SDK and the WebView2 runtime (Windows 11 already has it).

```
dotnet build MdReader.slnx
```

`installer\build.ps1` builds the installer (you'll need Inno Setup 6), and `dotnet run --project src\MdReader.Web` starts the web version locally.

Pull requests are built for you: the solution on Windows, and the web version's container image on Linux exactly the way it's deployed. Warnings are errors, so a new warning fails the build. Pushing a `v*` tag builds the installer and the VS Code extension and attaches them to that release. The workflows live in `.github/workflows`.

## License

MIT
