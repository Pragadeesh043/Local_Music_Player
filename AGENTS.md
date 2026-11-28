# Repository Guidelines

## Project Structure & Module Organization

MP4A is a Windows x64 music player. Open `MP4A.slnx` for the C# WinUI desktop host in `src/MP4A.Desktop`. Its `UI` directory owns the WebView2 host and bridge, `Audio` handles decoding, DSP, and playback, and `Library` handles catalog and artwork storage. Desktop icons and bundled native dependencies live in `Assets` and `Native`.

The React and TypeScript UI lives in `src/MP4A.Web/src`, with static assets in `public` and browser demo data in `demo` under `src/MP4A.Web`. `examples/native-host` contains an optional C++ bridge example. Installer and Store packaging files live in `packaging`, and development and release instructions are in `docs`.

## Build, Test, and Development Commands

Use Windows x64 with the .NET 10 SDK, Node.js 22.12+ or a supported newer version, and the WebView2 Evergreen Runtime.

- `./build.ps1` builds the web UI and desktop app. Launch `./artifacts/Release/MP4A.exe` to run it locally. Add `-Configuration Debug` for a debug build.
- From `src/MP4A.Web`, run `npm.cmd ci` to install dependencies, `npm.cmd run dev` for the browser demo, and `npm.cmd run build` for TypeScript checking and a production Vite build.
- `./build-app.ps1` builds the NSIS installer. For MSIX packaging and required FFmpeg inputs, follow [docs/release.md](docs/release.md).

## Coding Style & Naming Conventions

Use four spaces and file-scoped namespaces in C#, with PascalCase types and methods. Use two spaces, single quotes, and semicolon-free statements in TypeScript. Name React component files in PascalCase (`NowPlaying.tsx`) and utility modules in camelCase (`artworkResource.ts`). Keep bridge payload changes aligned across both projects. Use the existing build checks and match nearby formatting.

## Testing Guidelines

The repository has no configured automated test suite or coverage threshold. Build the affected projects and smoke-test UI changes in the browser demo. Verify bridge, library, and playback changes in the desktop app with synthetic fixtures and an absolute `MP4A_DATA_DIRECTORY` pointing to an isolated profile. Redact paths and track names from logs.

## Commit & Pull Request Guidelines

Follow recent history with short, imperative subjects such as `Fix DSP apply and add EQ curve presets`. Keep pull requests focused, explain the change and verification performed, link related issues when available, and attach screenshots for visible UI changes.
