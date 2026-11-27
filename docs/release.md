# Release checklist

## GitHub

1. Build the x64 installer with `./build.ps1 -Installer -OutputDirectory <output> -FFmpegDirectory <path-to-extracted-Tools/FFmpeg>`. Use the checksum-verified FFmpeg input from the release kit. Check the installer, checksum, and [third-party notices](../THIRD-PARTY-NOTICES.md).
2. Verify clean install and upgrade in an isolated Windows profile. Check the library, playback, and app icon. Silent install uses the NSIS `/S` switch.
3. Choose the version and tag for the release. Attach `MP4A-Setup-x64.exe` and its SHA-256 checksum to the matching GitHub release. Mention the WebView2 prerequisite and bundled FFmpeg build in the release description.
4. Review repository history before making it public. Earlier commits contain a SACD helper binary that was removed from current source. Review those commits and their distribution terms before exposing that history.
5. Before publishing a bundled FFmpeg build, include its upstream license notices and arrange distribution of the exact corresponding source and build information for the binaries and their linked libraries. [FFmpeg licensing guidance](https://ffmpeg.org/legal.html)

## Microsoft Store

Build the x64 Store package with `./build-store.ps1 -OutputDirectory .verify/store -FFmpegDirectory <path-to-extracted-Tools/FFmpeg>`. Use the checksum-verified MP4A FFmpeg 8.0.3 Windows x64 input ZIP from the release kit. The script uses MakeAppx from the restored Windows SDK BuildTools package or accepts a path through `-MakeAppx`. It outputs an MSIX and SHA-256 checksum.

- Confirm the package identity and version in `packaging/AppxManifest.xml` against Partner Center. Test a locally signed copy on a clean Windows profile with WebView2, and run the Windows App Certification Kit. Upload the original Store package through Partner Center. [MSIX readiness checklist](https://learn.microsoft.com/en-us/windows/apps/publish/store-readiness-checklist)
- Prepare the listing, privacy policy URL, and full-trust capability explanation. Declare WebView2 and verify the bundled FFmpeg distribution requirements from the release checklist above.
- Upload the generated `MP4A-Store-Tile-300.png` as the 1:1 app tile in Partner Center to match the packaged and in-app logo.
- Publish the matching `MP4A-FFmpeg-8.0.3-win64-source.tar.gz` from the release kit as a free download at the release URL in the bundled FFmpeg README. Make that source download easy to find from the Store listing or its support page.
- The Store package includes the SACD helper's upstream instructions. SACD ISO import requires a separately obtained helper on PATH.

### EXE submission

For an EXE submission, use Partner Center to submit a direct, versioned HTTPS URL for the installer. The URL must serve the same bytes for that version. Microsoft requires an offline installer with silent install support and trusted code signatures for the installer and its executable payload. [Microsoft Store EXE requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)

- Audit the installed payload signatures, including bundled third-party binaries, and arrange code signing before submission. Generate the release checksum after signing.
- Supply the silent install argument `/S` in Partner Center. Verify install and removal with the signed installer. [Microsoft Store certification process](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-certification-process)
- Declare the WebView2 dependency and identify bundled FFmpeg components in the submission materials. [Microsoft Store certification process](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-certification-process)

## Release notes draft

- Refreshed Discover and Now Playing with album-art lighting, live signal telemetry, and quicker access to DSP and output controls.
- Improved PCM pause and resume in WASAPI shared and exclusive output.
- Stored indexed album covers and previews locally for browsing and restart.
- Restored the previous Browse scroll position when returning from an album.
- Organized the Windows app, web interface, and optional C++ example into clearly named projects.
- Added a Start menu shortcut and a Windows Installed apps entry with uninstall support.
