cmake_minimum_required(VERSION 3.25)
project(Glassroom LANGUAGES CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Glassroom requires an x64 Windows build.")
endif()

set(WEBVIEW2_ROOT "" CACHE PATH "Extracted Microsoft.Web.WebView2 NuGet package")
set(NLOHMANN_INCLUDE_DIR "" CACHE PATH "Optional directory containing nlohmann/json.hpp")
set(UI_DIST "${CMAKE_CURRENT_SOURCE_DIR}/../ui/dist" CACHE PATH "Built Vite UI")
if(NOT EXISTS "${WEBVIEW2_ROOT}/build/native/include/WebView2.h")
    message(FATAL_ERROR "Set WEBVIEW2_ROOT to the extracted WebView2 NuGet package.")
endif()
if(NOT EXISTS "${UI_DIST}/index.html")
    message(FATAL_ERROR "Build the UI first with npm run build.")
endif()

if(NLOHMANN_INCLUDE_DIR)
    add_library(glassroom_json INTERFACE)
    target_include_directories(glassroom_json INTERFACE "${NLOHMANN_INCLUDE_DIR}")
    add_library(nlohmann_json::nlohmann_json ALIAS glassroom_json)
else()
    find_package(nlohmann_json 3.12 CONFIG QUIET)
    if(NOT nlohmann_json_FOUND)
        include(FetchContent)
        set(JSON_BuildTests OFF CACHE INTERNAL "")
        FetchContent_Declare(json
            GIT_REPOSITORY https://github.com/nlohmann/json.git
            GIT_TAG v3.12.0
            GIT_SHALLOW TRUE
        )
        FetchContent_MakeAvailable(json)
    endif()
endif()

set(WEBVIEW2_NATIVE "${WEBVIEW2_ROOT}/build/native")
add_executable(Glassroom WIN32 Main.cpp Worker.cpp Protocol.cpp DemoEngine.cpp)
target_compile_features(Glassroom PRIVATE cxx_std_20)
target_compile_definitions(Glassroom PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN
    WINVER=0x0A00 _WIN32_WINNT=0x0A00)
target_include_directories(Glassroom PRIVATE "${WEBVIEW2_NATIVE}/include")
target_link_libraries(Glassroom PRIVATE nlohmann_json::nlohmann_json
    "${WEBVIEW2_NATIVE}/x64/WebView2Loader.dll.lib" dwmapi ole32 shell32 uuid user32 gdi32)
if(MSVC)
    target_compile_options(Glassroom PRIVATE /W4 /utf-8 /permissive- /EHsc)
elseif(MINGW)
    target_compile_options(Glassroom PRIVATE -Wall -Wextra -Wno-unknown-pragmas)
    target_link_options(Glassroom PRIVATE -municode -static)
endif()

add_custom_target(stage ALL
    COMMAND "${CMAKE_COMMAND}" -E copy_directory "${UI_DIST}" "$<TARGET_FILE_DIR:Glassroom>/ui"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CMAKE_CURRENT_SOURCE_DIR}/../demo/catalog.json" "$<TARGET_FILE_DIR:Glassroom>/catalog.json"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${WEBVIEW2_NATIVE}/x64/WebView2Loader.dll" "$<TARGET_FILE_DIR:Glassroom>/WebView2Loader.dll"
    DEPENDS Glassroom
    VERBATIM
)
