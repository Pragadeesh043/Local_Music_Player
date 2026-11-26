# Release checklist

## GitHub

1. Build the x64 installer with `./build-app.ps1`. Check the installer, checksum, and [third-party notices](../THIRD-PARTY-NOTICES.md).
2. Verify clean install and upgrade in an isolated Windows profile. Check the library, playback, and app icon. Silent install uses the NSIS `/S` switch.
3. Choose the version and tag for the release. Attach `MP4A-Setup-x64.exe` and its SHA-256 checksum to the matching GitHub release. Mention the WebView2 and FFmpeg prerequisites in the release description.
4. Review repository history before making it public. Earlier commits contain a SACD helper binary that was removed from current source. Review those commits and their distribution terms before exposing that history.

## Microsoft Store

For an EXE submission, use Partner Center to submit a direct, versioned HTTPS URL for the installer. The URL must serve the same bytes for that version. Microsoft requires an offline installer with silent install support and trusted code signatures for the installer and its executable payload. [Microsoft Store EXE requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)

- Audit the installed payload signatures, including bundled third-party binaries, and arrange code signing before submission. Generate the release checksum after signing.
- The current installer copies the app into the chosen folder. Add a registered Windows uninstall flow and verify clean removal before Store submission. Supply the silent install argument `/S` in Partner Center. [Microsoft Store certification process](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-certification-process)
- Declare the WebView2 and FFmpeg dependencies in the submission materials. [Microsoft Store certification process](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-certification-process)

## Release notes draft

- Refreshed Discover and Now Playing with album-art lighting, live signal telemetry, and quicker access to DSP and output controls.
- Improved PCM pause and resume in WASAPI shared and exclusive output.
- Stored indexed album covers and previews locally for browsing and restart.
- Restored the previous Browse scroll position when returning from an album.
- Organized the Windows app, web interface, and optional C++ example into clearly named projects.
