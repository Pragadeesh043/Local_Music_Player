# MP4A native host example

This optional C++ example runs the MP4A web interface inside WebView2 with a simulated backend. The production installer uses `src/MP4A.Desktop`.

Install the **Desktop development with C++** workload with an x64 MSVC compiler and Windows SDK, CMake 3.25+, Git, the WebView2 Evergreen Runtime, and Node.js 22.12+ or a supported newer version. Run these commands from `examples/native-host`.

```powershell
.\build-native.ps1
.\native\bin\Release\MP4ADemo.exe
```

The script builds `src/MP4A.Web` and stages its output and shared catalog fixtures beside the example executable. Pass `-WebView2Root` or `-JsonIncludeDirectory` to supply local copies of the dependencies. Otherwise the build uses its pinned WebView2 SDK and CMake can fetch nlohmann/json v3.12.0.

`native/Engine.hpp` defines the backend interface. `DemoEngine.cpp` implements the simulated catalog and playback clock. `Protocol.cpp` maps frontend commands and events to the backend, `Worker.cpp` serializes calls, and `Main.cpp` owns the example window.
