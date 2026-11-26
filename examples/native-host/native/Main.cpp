#include "Worker.hpp"

#include <windows.h>
#include <dwmapi.h>
#include <shlobj.h>
#include <wrl/client.h>
#include <WebView2.h>

#include <atomic>
#include <deque>
#include <filesystem>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <string_view>
#include <utility>

#ifdef __MINGW32__
// The SDK declares these IIDs with MIDL attributes. MinGW also needs UUID template specializations.
__CRT_UUID_DECL(ICoreWebView2Controller2, 0xc979903e, 0xd4ca, 0x4228, 0x92, 0xeb, 0x47, 0xee, 0x3f, 0xa9, 0x6e, 0xab)
__CRT_UUID_DECL(ICoreWebView2Settings3, 0xfdb5ab74, 0xaf33, 0x4854, 0x84, 0xf0, 0x0a, 0x63, 0x1d, 0xeb, 0x5e, 0xba)
__CRT_UUID_DECL(ICoreWebView2_3, 0xa0d6df20, 0x3b92, 0x416d, 0xaa, 0x0c, 0x43, 0x7a, 0x9c, 0x72, 0x78, 0x57)
__CRT_UUID_DECL(ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler, 0x4e8a3389, 0xc9d8, 0x4bd2, 0xb6, 0xb5, 0x12, 0x4f, 0xee, 0x6c, 0xc1, 0x4d)
__CRT_UUID_DECL(ICoreWebView2CreateCoreWebView2ControllerCompletedHandler, 0x6c4819f3, 0xc9b7, 0x4260, 0x81, 0x27, 0xc9, 0xf5, 0xbd, 0xe7, 0xf6, 0x8c)
__CRT_UUID_DECL(ICoreWebView2NavigationStartingEventHandler, 0x9adbe429, 0xf36d, 0x432b, 0x9d, 0xdc, 0xf8, 0x88, 0x1f, 0xbd, 0x76, 0xe3)
__CRT_UUID_DECL(ICoreWebView2NewWindowRequestedEventHandler, 0xd4c185fe, 0xc81c, 0x4989, 0x97, 0xaf, 0x2d, 0x3f, 0xa7, 0xab, 0x56, 0x51)
__CRT_UUID_DECL(ICoreWebView2WebMessageReceivedEventHandler, 0x57213f19, 0x00e6, 0x49fa, 0x8e, 0x07, 0x89, 0x8e, 0xa0, 0x1e, 0xcb, 0xd2)
#endif

namespace {
using namespace glassroom;
using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

constexpr auto WindowClass = L"GlassroomWindow";
constexpr auto AppUrl = L"https://glassroom.test/index.html";
constexpr UINT DrainMessage = WM_APP + 1;

template <class Interface, class Fn, class First, class Second>
ComPtr<Interface> makeCallback(Fn callback, HRESULT (STDMETHODCALLTYPE Interface::*)(First, Second))
{
    class Handler final : public Interface {
    public:
        explicit Handler(Fn fn) : callback_(std::move(fn)) {}

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** output) override
        {
            if (!output) return E_POINTER;
            *output = nullptr;
            if (iid != __uuidof(IUnknown) && iid != __uuidof(Interface)) return E_NOINTERFACE;
            *output = static_cast<Interface*>(this);
            AddRef();
            return S_OK;
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
        ULONG STDMETHODCALLTYPE Release() override
        {
            const auto remaining = --references_;
            if (!remaining) delete this;
            return remaining;
        }
        HRESULT STDMETHODCALLTYPE Invoke(First first, Second second) override
        {
            try { return callback_(first, second); }
            catch (...) { return E_FAIL; }
        }

    private:
        std::atomic<ULONG> references_{1};
        Fn callback_;
    };
    ComPtr<Interface> result;
    result.Attach(new Handler(std::move(callback)));
    return result;
}

template <class Interface, class Fn>
ComPtr<Interface> Callback(Fn callback)
{
    return makeCallback(std::move(callback), &Interface::Invoke);
}

std::wstring widen(std::string_view text)
{
    if (text.empty()) return {};
    const auto length = static_cast<int>(text.size());
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), length, nullptr, 0);
    std::wstring result(count, L'\0');
    if (count) MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), length, result.data(), count);
    return result;
}

std::string narrow(std::wstring_view text)
{
    if (text.empty()) return {};
    const auto length = static_cast<int>(text.size());
    const int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), length, nullptr, 0, nullptr, nullptr);
    std::string result(count, '\0');
    if (count) WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), length, result.data(), count, nullptr, nullptr);
    return result;
}

struct TaskStringDeleter { void operator()(wchar_t* value) const { CoTaskMemFree(value); } };
using TaskString = std::unique_ptr<wchar_t, TaskStringDeleter>;

fs::path executableFolder()
{
    std::wstring path(32768, L'\0');
    const auto size = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!size || size >= path.size()) throw std::runtime_error("Could not find the executable.");
    path.resize(size);
    return fs::path(path).parent_path();
}

fs::path profileFolder()
{
    PWSTR raw{};
    const auto result = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &raw);
    TaskString path(raw);
    if (FAILED(result)) throw std::runtime_error("Could not find Local App Data.");
    return fs::path(path.get()) / L"Glassroom";
}

void require(HRESULT result)
{
    if (FAILED(result)) throw std::runtime_error("WebView2 initialization failed (" + std::to_string(result) + ").");
}

class Host : public std::enable_shared_from_this<Host> {
public:
    Host(fs::path root, fs::path profile, bool acrylic)
        : root_(std::move(root)), profile_(std::move(profile)), acrylic_(acrylic) {}

    void attach(HWND window) noexcept { window_ = window; }

    void start(std::unique_ptr<Engine> engine)
    {
        // The HWND stays valid until close() has joined the native worker.
        worker_ = std::make_unique<Worker>(std::move(engine), [this](Json message, bool replaceable) {
            auto wire = widen(message.dump(-1, ' ', false, Json::error_handler_t::replace));
            std::lock_guard lock(outgoingMutex_);
            const bool shouldWake = outgoing_.empty();
            if (!outgoing_.empty() && outgoing_.back().second) outgoing_.pop_back();
            outgoing_.emplace_back(std::move(wire), replaceable);
            if (shouldWake) PostMessageW(window_, DrainMessage, 0, 0);
        });

        constexpr auto backdropAttribute = static_cast<DWMWINDOWATTRIBUTE>(38);
        const int material = acrylic_ ? 3 : 2;
        const MARGINS margins{-1, -1, -1, -1};
        transparent_ = SUCCEEDED(DwmSetWindowAttribute(window_, backdropAttribute, &material, sizeof(material)))
            && SUCCEEDED(DwmExtendFrameIntoClientArea(window_, &margins));
        setTheme(dark_);

        const auto weak = weak_from_this();
        require(CreateCoreWebView2EnvironmentWithOptions(nullptr, (profile_ / L"WebView2").c_str(), nullptr,
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
                [weak](HRESULT result, ICoreWebView2Environment* environment) -> HRESULT {
                    const auto host = weak.lock();
                    if (!host || !host->window_) return S_OK;
                    try {
                        require(result);
                        if (!environment) throw std::runtime_error("The WebView2 environment is unavailable.");
                        host->environment_ = environment;
                        require(environment->CreateCoreWebView2Controller(host->window_,
                            Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                                [weak](HRESULT status, ICoreWebView2Controller* controller) -> HRESULT {
                                    const auto current = weak.lock();
                                    if (!current || !current->window_) {
                                        if (controller) controller->Close();
                                        return S_OK;
                                    }
                                    try {
                                        require(status);
                                        if (!controller) throw std::runtime_error("The WebView2 controller is unavailable.");
                                        current->configure(controller);
                                    } catch (const std::exception& error) { current->showError(error.what()); }
                                    return S_OK;
                                }).Get()));
                    } catch (const std::exception& error) { host->showError(error.what()); }
                    return S_OK;
                }).Get()));
    }

    void showError(const char* message)
    {
        MessageBoxW(window_, widen(message).c_str(), L"MP4A Native Demo", MB_OK | MB_ICONERROR);
        if (window_) DestroyWindow(window_);
    }

    void resize()
    {
        if (!controller_) return;
        RECT rectangle{};
        GetClientRect(window_, &rectangle);
        controller_->put_Bounds(rectangle);
        controller_->put_IsVisible(!IsIconic(window_));
    }

    void moved() { if (controller_) controller_->NotifyParentWindowPositionChanged(); }

    void drain()
    {
        std::deque<std::pair<std::wstring, bool>> messages;
        {
            std::lock_guard lock(outgoingMutex_);
            messages.swap(outgoing_);
        }
        if (web_) for (const auto& [wire, progress] : messages) web_->PostWebMessageAsJson(wire.c_str());
    }

    void close()
    {
        worker_.reset();
        if (controller_) controller_->Close();
        web_.Reset();
        controller_.Reset();
        environment_.Reset();
        window_ = nullptr;
    }

private:
    void setTheme(bool dark)
    {
        dark_ = dark;
        constexpr auto captionTheme = static_cast<DWMWINDOWATTRIBUTE>(20);
        const BOOL enabled = dark ? TRUE : FALSE;
        DwmSetWindowAttribute(window_, captionTheme, &enabled, sizeof(enabled));
        if (background_) {
            const COREWEBVIEW2_COLOR color = transparent_ ? COREWEBVIEW2_COLOR{0, 0, 0, 0}
                : dark ? COREWEBVIEW2_COLOR{255, 8, 8, 8} : COREWEBVIEW2_COLOR{255, 242, 242, 242};
            background_->put_DefaultBackgroundColor(color);
        }
    }

    void configure(ICoreWebView2Controller* controller)
    {
        controller_ = controller;
        require(controller_->get_CoreWebView2(&web_));
        controller_.As(&background_);
        setTheme(dark_);
        resize();

        ComPtr<ICoreWebView2Settings> settings;
        require(web_->get_Settings(&settings));
        settings->put_AreDefaultContextMenusEnabled(FALSE);
        settings->put_AreDevToolsEnabled(FALSE);
        settings->put_IsStatusBarEnabled(FALSE);
        ComPtr<ICoreWebView2Settings3> versionedSettings;
        if (SUCCEEDED(settings.As(&versionedSettings))) versionedSettings->put_AreBrowserAcceleratorKeysEnabled(FALSE);

        ComPtr<ICoreWebView2_3> mapping;
        require(web_.As(&mapping));
        require(mapping->SetVirtualHostNameToFolderMapping(L"glassroom.test", (root_ / L"ui").c_str(),
                                                          COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY));
        EventRegistrationToken token{};
        require(web_->add_NavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
            [](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
                LPWSTR raw{};
                args->get_Uri(&raw);
                TaskString uri(raw);
                if (!uri || std::wstring_view(uri.get()) != AppUrl) args->put_Cancel(TRUE);
                return S_OK;
            }).Get(), &token));
        require(web_->add_NewWindowRequested(Callback<ICoreWebView2NewWindowRequestedEventHandler>(
            [](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT {
                return args->put_Handled(TRUE);
            }).Get(), &token));
        const auto weak = weak_from_this();
        require(web_->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>(
            [weak](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) -> HRESULT {
                if (const auto host = weak.lock(); host && host->window_) host->receive(args);
                return S_OK;
            }).Get(), &token));
        require(mapping->SetVirtualHostNameToFolderMapping(L"covers.glassroom.test", (profile_ / L"Artwork").c_str(),
                                                          COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_ALLOW));
        require(web_->Navigate(AppUrl));
    }

    void post(const Json& message)
    {
        const auto wire = widen(message.dump(-1, ' ', false, Json::error_handler_t::replace));
        web_->PostWebMessageAsJson(wire.c_str());
    }

    void receive(ICoreWebView2WebMessageReceivedEventArgs* args)
    {
        LPWSTR rawSource{}, rawMessage{};
        args->get_Source(&rawSource);
        TaskString source(rawSource);
        if (!source || std::wstring_view(source.get()) != AppUrl) return;
        if (FAILED(args->get_WebMessageAsJson(&rawMessage))) return;
        TaskString message(rawMessage);
        if (!message || std::wstring_view(message.get()).size() > 4096) return;
        const auto request = Json::parse(narrow(message.get()), nullptr, false);
        if (!request.is_object() || !request.contains("id") || !request["id"].is_number_integer()
            || request["id"] <= 0 || request["id"] > (std::numeric_limits<int>::max)()) return;
        const int id = request["id"].get<int>();
        try {
            if (request.at("v") != ProtocolVersion) throw std::invalid_argument("Unsupported bridge version.");
            auto method = request.at("method").get<std::string>();
            const auto& arguments = request.at("args");
            if (method == "theme") {
                setTheme(arguments.at("dark").get<bool>());
                post(success(id, nullptr));
            } else if (!worker_ || !worker_->submit(id, std::move(method), arguments)) {
                post(failure(id, "The player is busy. Try again."));
            }
        } catch (const std::exception&) { post(failure(id, "Invalid bridge request.")); }
    }

    fs::path root_, profile_;
    HWND window_{};
    bool acrylic_{}, transparent_{}, dark_{true};
    ComPtr<ICoreWebView2Environment> environment_;
    ComPtr<ICoreWebView2Controller> controller_;
    ComPtr<ICoreWebView2Controller2> background_;
    ComPtr<ICoreWebView2> web_;
    std::mutex outgoingMutex_;
    std::deque<std::pair<std::wstring, bool>> outgoing_;
    std::unique_ptr<Worker> worker_;
};

LRESULT CALLBACK windowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_NCCREATE) {
        const auto create = reinterpret_cast<const CREATESTRUCTW*>(lParam);
        auto host = static_cast<Host*>(create->lpCreateParams);
        host->attach(window);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(host));
    }
    const auto host = reinterpret_cast<Host*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (!host) return DefWindowProcW(window, message, wParam, lParam);

    switch (message) {
        case WM_GETMINMAXINFO: {
            auto info = reinterpret_cast<MINMAXINFO*>(lParam);
            const UINT dpi = GetDpiForWindow(window);
            info->ptMinTrackSize = {MulDiv(520, dpi, 96), MulDiv(445, dpi, 96)};
            return 0;
        }
        case WM_SIZE: host->resize(); return 0;
        case WM_MOVE: host->moved(); break;
        case WM_DPICHANGED: {
            const auto bounds = reinterpret_cast<const RECT*>(lParam);
            SetWindowPos(window, nullptr, bounds->left, bounds->top, bounds->right - bounds->left,
                bounds->bottom - bounds->top, SWP_NOACTIVATE | SWP_NOZORDER);
            return 0;
        }
        case DrainMessage: host->drain(); return 0;
        case WM_DESTROY: host->close(); PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

int run(HINSTANCE instance, int show, bool acrylic)
{
    const auto root = executableFolder();
    const auto profile = profileFolder();
    fs::create_directories(profile / L"Artwork");
    if (!fs::exists(root / L"ui" / L"index.html")) throw std::runtime_error("Build and stage the MP4A web UI first.");
    std::ifstream catalog(root / L"catalog.json");
    if (!catalog) throw std::runtime_error("The demo catalog is missing.");
    auto engine = makeDemoEngine(Json::parse(catalog));

    WNDCLASSW windowClass{};
    windowClass.hInstance = instance;
    windowClass.lpszClassName = WindowClass;
    windowClass.lpfnWndProc = windowProc;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    if (!RegisterClassW(&windowClass)) throw std::runtime_error("Could not register the player window.");
    const auto host = std::make_shared<Host>(root, profile, acrylic);
    const auto dpi = GetDpiForSystem();
    const auto window = CreateWindowExW(0, WindowClass, L"MP4A Native Demo", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
        CW_USEDEFAULT, CW_USEDEFAULT, MulDiv(1230, dpi, 96), MulDiv(820, dpi, 96), nullptr, nullptr, instance, host.get());
    if (!window) throw std::runtime_error("Could not create the player window.");
    try { host->start(std::move(engine)); }
    catch (const std::exception& error) { host->showError(error.what()); return 1; }
    ShowWindow(window, show);

    MSG message{};
    int result;
    while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (result < 0) DestroyWindow(window);
    return result < 0 ? 1 : static_cast<int>(message.wParam);
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int show)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized)) return 1;
    int result = 1;
    try { result = run(instance, show, std::wstring_view(commandLine) == L"--acrylic"); }
    catch (const std::exception& error) { MessageBoxW(nullptr, widen(error.what()).c_str(), L"MP4A Native Demo", MB_OK | MB_ICONERROR); }
    CoUninitialize();
    return result;
}
