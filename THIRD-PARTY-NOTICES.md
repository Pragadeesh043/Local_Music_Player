# Third-party notices

MP4A's original code is available under the [MIT license](LICENSE). The dependencies below retain their own terms.

- The desktop app uses .NET, the Windows App SDK, WebView2, NAudio, and Microsoft.Data.Sqlite with SQLitePCLRaw. See the package references in [MP4A.Desktop.csproj](src/MP4A.Desktop/MP4A.Desktop.csproj).
- The web interface uses React, Motion, TanStack Virtual, Tailwind CSS, and Lucide. See [package-lock.json](src/MP4A.Web/package-lock.json) for exact versions.
- The app bundles Steam Audio. See its [license](src/MP4A.Desktop/Native/SteamAudio-LICENSE.md) and [third-party notices](src/MP4A.Desktop/Native/SteamAudio-THIRDPARTY.md). The installer includes copies under `Native`.
- The optional C++ example uses the WebView2 SDK and nlohmann/json. See [the example build](examples/native-host/native/CMakeLists.txt).
- FFmpeg and sacd_extract are separate downloads. Their upstream terms apply. See the [installation guide](README.md).

The installer includes license files from the restored npm and NuGet packages under `licenses`.
