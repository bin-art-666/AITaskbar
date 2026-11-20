// ==WindhawkMod==
// @id              aitaskbar-ai
// @name            AIEnter AI beside clock
// @description     Adds an AI button in the real taskbar layout immediately after the clock.
// @version         3.0.0
// @author          AIEnter
// @include         explorer.exe
// @architecture    x86-64
// @compilerOptions -lole32 -loleaut32 -lruntimeobject -lshell32 -lshcore -luuid
// ==/WindhawkMod==
// SPDX-License-Identifier: MIT
// Taskbar discovery, dispatch, XAML access and slot leasing adapted from
// Taskbar Folder Menus 2.1, copyright (c) 2026 sb4ssman and contributors.
// https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-folder-menus.wh.cpp
// See ThirdParty/TaskbarIntegration-MIT.txt in the AIEnter source distribution.

// ==WindhawkModSettings==
/*
- executable: ""
  $name: AITaskbar executable path
- commandPrefix: ""
  $name: AITaskbar AI command prefix
- dataDirectory: ""
  $name: AITaskbar AI profile directory
*/
// ==/WindhawkModSettings==
#include <windows.h>
#include <shellapi.h>
#include <shcore.h>
#include <shlobj.h>
#include <knownfolders.h>
#include <windhawk_utils.h>
#undef GetCurrentTime
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.Input.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Composition.h>
#include <cmath>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Media.Imaging.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <winrt/Windows.ApplicationModel.DataTransfer.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Data.Json.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <string>
#include <thread>
#include <vector>
namespace folder_menus_slot_lease {

using winrt::Windows::UI::Xaml::FrameworkElement;
using winrt::Windows::UI::Xaml::GridUnitType;
using winrt::Windows::UI::Xaml::Controls::ColumnDefinition;
using winrt::Windows::UI::Xaml::Controls::Grid;
using winrt::Windows::UI::Xaml::Controls::Panel;
using winrt::Windows::UI::Xaml::Controls::StackPanel;

enum class Anchor {
    BeforeIcons,
    BeforeOmni,
    BeforeClock,
    AfterClock,
    AfterShowDesktop,
};

// Which layout contract the live tray panel follows.
enum class Kind {
    Unsupported,  // some other Panel: do not guess its layout semantics.
    Columns,      // Grid. A slot is a column index.
    Order,        // StackPanel. A slot is a child index.
};

struct Lease {
    std::wstring markerName;
    int slot = -1;
    Kind kind = Kind::Unsupported;
};

inline Kind Classify(FrameworkElement const& parent) {
    if (!parent)
        return Kind::Unsupported;
    if (parent.try_as<Grid>())
        return Kind::Columns;
    if (parent.try_as<StackPanel>())
        return Kind::Order;
    return Kind::Unsupported;
}

// Class name of an unexpected panel, so a mod can log what it actually got
// instead of reporting a bare "not found" for an element that is right there.
inline std::wstring ClassName(FrameworkElement const& element) {
    if (!element)
        return L"(null)";
    try {
        return std::wstring(winrt::get_class_name(element));
    } catch (...) {
        return L"(unknown)";
    }
}

// Named direct children, for logging when an anchor cannot be resolved. A tray
// restructure shows up here as missing or renamed names, which is the one thing
// a user's debug log otherwise cannot tell us.
inline std::wstring DescribeChildren(Panel const& parent) {
    if (!parent)
        return L"(none)";
    std::wstring names;
    try {
        for (auto const& child : parent.Children()) {
            auto element = child.try_as<FrameworkElement>();
            if (!element || element.Name().empty())
                continue;
            if (!names.empty())
                names += L", ";
            names += element.Name();
        }
    } catch (...) {
        return L"(unreadable)";
    }
    return names.empty() ? L"(no named children)" : names;
}

inline FrameworkElement FindDirectChild(Panel const& parent,
                                        wchar_t const* name) {
    if (!parent || !name)
        return nullptr;
    for (auto const& child : parent.Children()) {
        auto element = child.try_as<FrameworkElement>();
        if (element && element.Name() == name)
            return element;
    }
    return nullptr;
}

inline int IndexOfChild(Panel const& parent, FrameworkElement const& child) {
    if (!parent || !child)
        return -1;
    for (uint32_t i = 0; i < parent.Children().Size(); ++i) {
        if (parent.Children().GetAt(i).try_as<FrameworkElement>() == child)
            return static_cast<int>(i);
    }
    return -1;
}

inline bool ResolveSlot(Panel const& parent, Anchor anchor, int& slot) {
    Kind kind = Classify(parent);
    if (kind == Kind::Unsupported)
        return false;

    if (anchor == Anchor::BeforeIcons) {
        slot = 0;
        return true;
    }

    wchar_t const* referenceName = nullptr;
    bool after = false;
    switch (anchor) {
        case Anchor::BeforeOmni:
            referenceName = L"ControlCenterButton";
            break;
        case Anchor::BeforeClock:
            referenceName = L"NotificationCenterButton";
            break;
        case Anchor::AfterClock:
            referenceName = L"NotificationCenterButton";
            after = true;
            break;
        case Anchor::AfterShowDesktop:
            referenceName = L"ShowDesktopStack";
            after = true;
            break;
        case Anchor::BeforeIcons:
            break;
    }

    auto reference = FindDirectChild(parent, referenceName);
    if (!reference)
        return false; // Never silently turn an unavailable anchor into slot 0.

    if (kind == Kind::Columns) {
        slot = Grid::GetColumn(reference) +
               (after ? std::max(1, Grid::GetColumnSpan(reference)) : 0);
        return true;
    }

    // Order: the reference's own child index is the slot. There is no span to
    // step over -- a StackPanel child occupies exactly one position.
    int index = IndexOfChild(parent, reference);
    if (index < 0)
        return false;
    slot = index + (after ? 1 : 0);
    return true;
}

inline bool Release(Panel const& parent, Lease& lease);

inline bool AcquireAt(Panel const& parent, int slot,
                      std::wstring const& markerName, Lease& lease) {
    Kind kind = Classify(parent);
    if (kind == Kind::Unsupported || slot < 0 || markerName.empty())
        return false;

    // A marker with this name already in the tray is either this caller's own
    // live lease — refuse, acquiring twice would strand the first slot — or a
    // leftover from an instance whose teardown never reached the UI thread.
    // Refusing the leftover would block every later apply until Explorer
    // restarts, so release it and take the slot fresh, correcting the
    // requested slot if the released one sat before it.
    if (auto stale = FindDirectChild(parent, markerName.c_str())) {
        if (lease.markerName == markerName)
            return false;
        int staleSlot = kind == Kind::Columns
                            ? Grid::GetColumn(stale)
                            : IndexOfChild(parent, stale);
        Lease leftover{markerName, staleSlot, kind};
        if (!Release(parent, leftover))
            return false;
        if (staleSlot >= 0 && staleSlot < slot)
            --slot;
    }

    Grid marker;
    marker.Name(markerName);
    marker.Width(0.0);
    marker.Height(0.0);
    marker.IsHitTestVisible(false);

    if (kind == Kind::Columns) {
        auto grid = parent.try_as<Grid>();
        if (!grid)
            return false;
        ColumnDefinition definition;
        definition.Width({1.0, GridUnitType::Auto});
        if (static_cast<uint32_t>(slot) < grid.ColumnDefinitions().Size())
            grid.ColumnDefinitions().InsertAt(slot, definition);
        else
            grid.ColumnDefinitions().Append(definition);

        for (auto const& child : grid.Children()) {
            auto element = child.try_as<FrameworkElement>();
            if (!element) continue;
            int start = Grid::GetColumn(element);
            int span = Grid::GetColumnSpan(element);
            if (start >= slot)
                Grid::SetColumn(element, start + 1);
            else if (start + span > slot)
                Grid::SetColumnSpan(element, span + 1);
        }

        Grid::SetColumn(marker, slot);
        grid.Children().Append(marker);
    } else {
        // Order: no column is created or owned. The marker simply holds the
        // position, and PlaceChild drops the content next to it.
        uint32_t index = std::min(static_cast<uint32_t>(slot),
                                  parent.Children().Size());
        parent.Children().InsertAt(index, marker);
        slot = static_cast<int>(index);
    }

    lease = {markerName, slot, kind};
    return true;
}

// Live index of the lease marker. Other mods inject and remove siblings around
// us, so the acquire-time index is a hint, never the truth at removal time.
inline bool FindMarker(Panel const& parent, Lease const& lease,
                       uint32_t& index) {
    if (!parent || lease.markerName.empty())
        return false;
    for (uint32_t i = 0; i < parent.Children().Size(); ++i) {
        auto element = parent.Children().GetAt(i).try_as<FrameworkElement>();
        if (element && element.Name() == lease.markerName) {
            index = i;
            return true;
        }
    }
    return false;
}

// Put mod content into the leased slot. On a Grid the content joins the leased
// column; on a StackPanel it is inserted directly after the marker, so the
// marker's position is the content's position.
inline bool PlaceChild(Panel const& parent, Lease const& lease,
                       FrameworkElement const& content) {
    if (!parent || !content || lease.kind == Kind::Unsupported)
        return false;

    uint32_t markerIndex = 0;
    if (!FindMarker(parent, lease, markerIndex))
        return false;

    if (lease.kind == Kind::Columns) {
        auto marker = parent.Children().GetAt(markerIndex)
                          .try_as<FrameworkElement>();
        Grid::SetColumn(content, marker ? Grid::GetColumn(marker) : lease.slot);
        parent.Children().Append(content);
        return true;
    }

    parent.Children().InsertAt(markerIndex + 1, content);
    return true;
}

inline bool Release(Panel const& parent, Lease& lease) {
    if (!parent || lease.markerName.empty())
        return false;

    uint32_t markerIndex = 0;
    if (!FindMarker(parent, lease, markerIndex))
        return false;

    if (lease.kind == Kind::Order) {
        parent.Children().RemoveAt(markerIndex);
        lease = {};
        return true;
    }

    auto grid = parent.try_as<Grid>();
    auto marker =
        parent.Children().GetAt(markerIndex).try_as<FrameworkElement>();
    int liveColumn = marker ? Grid::GetColumn(marker) : lease.slot;
    if (!grid || liveColumn < 0)
        return false;

    grid.Children().RemoveAt(markerIndex);
    if (static_cast<uint32_t>(liveColumn) < grid.ColumnDefinitions().Size())
        grid.ColumnDefinitions().RemoveAt(liveColumn);

    for (auto const& child : grid.Children()) {
        auto element = child.try_as<FrameworkElement>();
        if (!element) continue;
        int start = Grid::GetColumn(element);
        int span = Grid::GetColumnSpan(element);
        if (start > liveColumn)
            Grid::SetColumn(element, start - 1);
        else if (start < liveColumn && start + span > liveColumn)
            Grid::SetColumnSpan(element, std::max(1, span - 1));
    }

    lease = {};
    return true;
}

}  // namespace folder_menus_slot_lease

namespace folder_menus_taskbar_window {

// ---- Window discovery -------------------------------------------------------

inline HWND FindCurrentProcessTaskbarWnd() {
    HWND result = nullptr;
    EnumWindows(
        [](HWND window, LPARAM parameter) -> BOOL {
            DWORD processId = 0;
            WCHAR className[32];
            if (GetWindowThreadProcessId(window, &processId) &&
                processId == GetCurrentProcessId() &&
                GetClassName(window, className, ARRAYSIZE(className)) &&
                _wcsicmp(className, L"Shell_TrayWnd") == 0) {
                *reinterpret_cast<HWND*>(parameter) = window;
                return FALSE;
            }
            return TRUE;
        },
        reinterpret_cast<LPARAM>(&result));
    return result;
}

// Shell_TrayWnd can be recreated inside Explorer. A cache is useful only while
// it names a live window; otherwise rediscover before dispatch or teardown.
inline HWND ResolveTaskbarWnd(HWND cached) {
    if (cached && IsWindow(cached))
        return cached;
    return FindCurrentProcessTaskbarWnd();
}

}  // namespace folder_menus_taskbar_window

namespace folder_menus_dispatch {

// ---- UI-thread marshalling --------------------------------------------------
//
// XAML may only be touched from the thread that owns it. This posts work onto
// the taskbar's thread with a CALLWNDPROC hook and a private registered
// message, and reports whether the callback actually ran — a caller that
// assumes it did will corrupt its own state when the dispatch failed.

using ThreadProc = void (*)(void*);
using ExceptionLogFn = void (*)(PCWSTR context);

inline ExceptionLogFn g_logException = nullptr;

// Point this at the mod's logger once in Wh_ModInit so failures inside a UI
// callback are reported in the mod's own voice.
inline void SetExceptionLogger(ExceptionLogFn logger) {
    g_logException = logger;
}

inline bool Invoke(ThreadProc proc, void* parameter) {
    try {
        proc(parameter);
        return true;
    } catch (...) {
        if (g_logException) g_logException(L"UI callback");
    }
    return false;
}

struct Dispatch {
    ThreadProc proc;
    void* parameter;
    bool succeeded = false;
    // Every concurrent caller installs its own hook with this same proc, and
    // each hook instance sees every message equal to g_dispatchMessage. With
    // two dispatches in flight, both hooks are in the chain when either
    // message arrives, so without this each callback would run twice. The
    // hooks run one after another on the UI thread, so a plain flag suffices.
    bool ran = false;
};

// The private message this mod dispatches on. Set before the hook is
// installed, and read by the hook proc to recognise its own message.
//
// A CALLWNDPROC HOOK SEES EVERY MESSAGE SENT TO EVERY WINDOW ON THE TASKBAR'S
// UI THREAD. `lParam` for all of those is arbitrary — an integer, a flag, a
// pointer to something else entirely. So the message MUST be checked first,
// against a value that does not come from lParam, and only then may lParam be
// treated as a Dispatch*. Reading anything out of lParam before that check
// dereferences whatever happened to be in the message and takes Explorer down
// with it.
//
// Atomic because the caller may be the retry thread while the hook
// proc runs on the taskbar's UI thread. RegisterWindowMessageW returns the
// same value for the same string for the lifetime of the session, so this
// settles on one value immediately and never changes again.
inline std::atomic<UINT> g_dispatchMessage{0};

// messageName must embed WH_MOD_ID, so two mods cannot collide on the message.
inline bool RunFromWindowThread(HWND window, ThreadProc proc, void* parameter,
                                PCWSTR messageName) {
    UINT message = RegisterWindowMessageW(messageName);
    if (!message) return false;

    DWORD threadId = GetWindowThreadProcessId(window, nullptr);
    if (!threadId) return false;
    if (threadId == GetCurrentThreadId()) return Invoke(proc, parameter);

    g_dispatchMessage.store(message, std::memory_order_release);

    HHOOK hook = SetWindowsHookExW(
        WH_CALLWNDPROC,
        [](int code, WPARAM wParam, LPARAM lParam) -> LRESULT {
            if (code == HC_ACTION) {
                auto const* call = reinterpret_cast<CWPSTRUCT const*>(lParam);
                // Message first. Only our own private message carries a
                // Dispatch* in lParam; everything else carries something we
                // must not touch.
                UINT expected =
                    g_dispatchMessage.load(std::memory_order_acquire);
                if (expected && call->message == expected) {
                    if (auto* dispatch =
                            reinterpret_cast<Dispatch*>(call->lParam);
                        dispatch && !dispatch->ran) {
                        dispatch->ran = true;
                        dispatch->succeeded =
                            Invoke(dispatch->proc, dispatch->parameter);
                    }
                }
            }
            return CallNextHookEx(nullptr, code, wParam, lParam);
        },
        nullptr, threadId);
    if (!hook) return false;

    Dispatch dispatch{proc, parameter};
    SendMessageW(window, message, 0, reinterpret_cast<LPARAM>(&dispatch));
    UnhookWindowsHookEx(hook);
    return dispatch.succeeded;
}

}  // namespace folder_menus_dispatch

namespace dispatch = folder_menus_dispatch;
namespace folder_menus_taskbar_xaml {

using winrt::Windows::UI::Xaml::FrameworkElement;
using winrt::Windows::UI::Xaml::XamlRoot;

// ---- XamlRoot ---------------------------------------------------------------

using CTaskBand_GetTaskbarHost_t = void*(WINAPI*)(void*, void*);
using TaskbarHost_FrameHeight_t = int(WINAPI*)(void*);
using Ref_count_base_Decref_t = void(WINAPI*)(void*);
using TrayUI_StartTaskbar_t = void(WINAPI*)(void*);

inline CTaskBand_GetTaskbarHost_t CTaskBand_GetTaskbarHost_Original = nullptr;
inline TaskbarHost_FrameHeight_t TaskbarHost_FrameHeight_Original = nullptr;
inline Ref_count_base_Decref_t Ref_count_base_Decref_Original = nullptr;
inline TrayUI_StartTaskbar_t TrayUI_StartTaskbar_Original = nullptr;
inline void* CTaskBand_ITaskListWndSite_vftable = nullptr;

// The mod's rebuild callback, invoked after Explorer rebuilds the taskbar.
inline void (*g_onTaskbarRebuilt)() = nullptr;

inline void WINAPI TrayUI_StartTaskbar_Hook(void* self) {
    TrayUI_StartTaskbar_Original(self);
    try {
        if (g_onTaskbarRebuilt) g_onTaskbarRebuilt();
    } catch (...) {
        if (dispatch::g_logException)
            dispatch::g_logException(L"TrayUI::StartTaskbar hook");
    }
}

inline bool HookTaskbarSymbols(void (*onTaskbarRebuilt)()) {
    g_onTaskbarRebuilt = onTaskbarRebuilt;
    HMODULE taskbar = LoadLibraryExW(L"taskbar.dll", nullptr,
                                     LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!taskbar) return false;
    WindhawkUtils::SYMBOL_HOOK taskbarDllHooks[] = {
        {{LR"(const CTaskBand::`vftable'{for `ITaskListWndSite'})"},
         &CTaskBand_ITaskListWndSite_vftable},
        {{LR"(public: virtual class std::shared_ptr<class TaskbarHost> __cdecl CTaskBand::GetTaskbarHost(void)const )"},
         &CTaskBand_GetTaskbarHost_Original},
        {{LR"(public: int __cdecl TaskbarHost::FrameHeight(void)const )"},
         &TaskbarHost_FrameHeight_Original},
        {{LR"(public: void __cdecl std::_Ref_count_base::_Decref(void))"},
         &Ref_count_base_Decref_Original},
        {{LR"(public: virtual void __cdecl TrayUI::StartTaskbar(void))"},
         &TrayUI_StartTaskbar_Original, TrayUI_StartTaskbar_Hook},
    };
    return WindhawkUtils::HookSymbols(taskbar, taskbarDllHooks,
                                      ARRAYSIZE(taskbarDllHooks));
}

// The FrameworkElement lives at an offset inside TaskbarHost that MOVES
// between Windows builds, so it is read out of TaskbarHost::FrameHeight's
// prologue at runtime rather than hardcoded.
inline size_t FrameworkElementOffset() {
    size_t offset = 0x10;
#if defined(_M_X64)
    BYTE const* code =
        reinterpret_cast<BYTE const*>(TaskbarHost_FrameHeight_Original);
    if (code[0] == 0x48 && code[1] == 0x83 && code[2] == 0xEC &&
        code[4] == 0x48 && code[5] == 0x83 && code[6] == 0xC1 &&
        code[7] <= 0x7F) {
        offset = code[7];
    }
#elif defined(_M_ARM64)
    DWORD const* code =
        reinterpret_cast<DWORD const*>(TaskbarHost_FrameHeight_Original);
    if (code[0] == 0xD503237F && (code[1] & 0xFFC07FFF) == 0xA9807BFD &&
        code[2] == 0x910003FD && (code[3] & 0xFFF00FE0) == 0xF8400C00) {
        offset = (code[3] >> 12) & 0xFF;
    }
#else
#error "Unsupported architecture"
#endif
    return offset;
}

inline XamlRoot GetTaskbarXamlRoot(HWND taskbarWnd) {
    if (!CTaskBand_GetTaskbarHost_Original ||
        !TaskbarHost_FrameHeight_Original || !Ref_count_base_Decref_Original ||
        !CTaskBand_ITaskListWndSite_vftable)
        return nullptr;

    HWND taskSwWnd = (HWND)GetProp(taskbarWnd, L"TaskbandHWND");
    if (!taskSwWnd) return nullptr;
    void* taskBand = (void*)GetWindowLongPtr(taskSwWnd, 0);
    if (!taskBand) return nullptr;

    void* site = taskBand;
    for (int i = 0; *(void**)site != CTaskBand_ITaskListWndSite_vftable; ++i) {
        if (i == 20) return nullptr;
        site = (void**)site + 1;
    }

    void* host[2]{};
    CTaskBand_GetTaskbarHost_Original(site, host);
    if (!host[0] || !host[1]) {
        if (host[1]) Ref_count_base_Decref_Original(host[1]);
        return nullptr;
    }

    auto* unknown =
        *(IUnknown**)((BYTE*)host[0] + FrameworkElementOffset());
    if (!unknown) {
        Ref_count_base_Decref_Original(host[1]);
        return nullptr;
    }
    FrameworkElement element = nullptr;
    unknown->QueryInterface(winrt::guid_of<FrameworkElement>(),
                            winrt::put_abi(element));
    auto result = element ? element.XamlRoot() : nullptr;
    Ref_count_base_Decref_Original(host[1]);
    return result;
}

}  // namespace folder_menus_taskbar_xaml

namespace xaml = winrt::Windows::UI::Xaml;
namespace controls = winrt::Windows::UI::Xaml::Controls;
namespace slot = folder_menus_slot_lease;
namespace taskbar = folder_menus_taskbar_xaml;

static HANDLE g_stop = nullptr, g_refresh = nullptr, g_appearanceEvent = nullptr, g_mapping = nullptr;
static volatile LONG* g_shared = nullptr;
struct AppearanceState { LONG magic = 0x41494531, pid = 0, icon = 26, enabled = 1, range = 9, intensity = 77, open = 0, sequence = 0, flowSpeed = 200, screenshot = 1; };
static AppearanceState g_appearance;
static AppearanceState ReadAppearance() {
    AppearanceState state;
    for (int attempt = 0; attempt < 32 && g_shared; ++attempt) {
        LONG before = g_shared[7]; if (before & 1) { Sleep(1); continue; }
        MemoryBarrier();
        LONG values[10]; for (int i = 0; i < 10; ++i) values[i] = g_shared[i];
        MemoryBarrier();
        if (before != g_shared[7]) { Sleep(1); continue; }
        if (values[0] != 0x41494531) break;
        state = {values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9] == 2 ? 0 : 1};
        break;
    }
    state.flowSpeed = std::clamp(state.flowSpeed, 0L, 1800L);
    state.icon = std::clamp(state.icon, 16L, 36L); state.range = std::clamp(state.range, 0L, 12L);
    state.intensity = std::clamp(state.intensity, 0L, 100L);
    return state;
}
static std::thread g_worker;
static std::wstring g_executable, g_verifyDropDirectory, g_commandPrefix, g_profileDirectory;
static std::atomic<bool> g_unloading{false};
static std::atomic<int> g_pendingDrops{0};
// XAML references must be released on the taskbar thread, never at DLL teardown.
struct AttachedButton {
    controls::Panel parent{nullptr};
    controls::Button button{nullptr};
    controls::Grid glyphHost{nullptr}, glow{nullptr};
    controls::Canvas glowOverlay{nullptr};
    xaml::Media::TranslateTransform glowPosition{nullptr};
    xaml::Shapes::Ellipse icon{nullptr};
    xaml::Media::ImageBrush staticFill{nullptr};
    int lastX = -1, lastRight = -1, lastWidth = -1;
    slot::Lease lease;
    winrt::event_token click{}, rightTap{}, layout{}, dragEnter{}, dragOver{}, dragLeave{}, drop{}, hoverTick{};
    winrt::Windows::Foundation::IInspectable pointerPress{nullptr};
    xaml::DispatcherTimer hoverTimer{nullptr}, flowTimer{nullptr}, clickTimer{nullptr};
    xaml::Media::Imaging::WriteableBitmap flowBitmap{nullptr};
    winrt::event_token flowTick{}, clickTick{};
    bool clickPending = false;
    DWORD pendingClickTime = 0; POINT pendingClickPoint{};
    ULONGLONG screenshotAt = 0;
    ULONGLONG flowStarted = 0;
    ULONGLONG nextFlowCheck = 0, nextFrameReport = 0;
    std::wstring flowStatus;
    bool systemAnimations = false, animationPolicyKnown = false;
    double flowTime = 0;
    xaml::Media::Brush normalBorder{nullptr};
    xaml::Thickness normalThickness{};
    bool dragging = false;
    DWORD pressTime = 0; POINT pressPoint{};
    ULONGLONG lastToggleAt = 0;
};
static AttachedButton* g_attached = nullptr;
static void LogFailure(PCWSTR context);
static void UpdateButtonAppearance() {
    if (!g_attached) return;
    auto& state = *g_attached;
    double availableHeight = std::max(16.0, state.parent.ActualHeight() - 4);
    double iconSize = std::min((double)g_appearance.icon, availableHeight);
    double range = g_appearance.enabled ? (double)g_appearance.range : 0;
    double extent = iconSize + 2 * range;
    double diameter = iconSize + 4;
    state.button.Width(diameter); state.button.Height(diameter);
    state.button.CornerRadius({diameter / 2, diameter / 2, diameter / 2, diameter / 2});
    auto buttonVisual = xaml::Hosting::ElementCompositionPreview::GetElementVisual(state.button);
    auto buttonClip = buttonVisual.Compositor().CreateEllipseGeometry();
    buttonClip.Center({(float)(diameter / 2), (float)(diameter / 2)});
    buttonClip.Radius({(float)(diameter / 2), (float)(diameter / 2)});
    buttonVisual.Clip(buttonVisual.Compositor().CreateGeometricClip(buttonClip));
    state.glyphHost.Width(iconSize); state.glyphHost.Height(iconSize);
    state.glow.Width(extent); state.glow.Height(std::min(extent, availableHeight));
    xaml::Media::RectangleGeometry clip;
    clip.Rect({0, 0, (float)extent, (float)std::min(extent, availableHeight)});
    state.glow.Clip(clip);
    state.icon.Width(iconSize); state.icon.Height(iconSize);
    state.glow.Children().Clear();
    for (int layer = 0; layer < 16; ++layer) {
        double fraction = (16 - layer) / 16.0;
        winrt::Windows::UI::Xaml::Shapes::Ellipse halo;
        halo.Width(iconSize + 2 * range * fraction); halo.Height(iconSize + 2 * range * fraction);
        halo.HorizontalAlignment(xaml::HorizontalAlignment::Center); halo.VerticalAlignment(xaml::VerticalAlignment::Center);
        halo.Fill(xaml::Media::SolidColorBrush(winrt::Windows::UI::Color{(BYTE)(5 + layer / 3), 115, 145, 255}));
        state.glow.Children().Append(halo);
    }
    bool active = g_appearance.open && g_appearance.enabled && range > 0 && g_appearance.intensity > 0;
    state.glow.Opacity(active ? g_appearance.intensity / 100.0 : 0.0);
    Wh_SetIntValue(L"ScreenshotDoubleClickEnabled", g_appearance.screenshot);
    Wh_SetIntValue(L"DoubleClickMilliseconds", GetDoubleClickTime());
    Wh_SetIntValue(L"ColorFlowSpeed", g_appearance.flowSpeed);
    Wh_SetIntValue(L"GlowActive", active ? 1 : 0);
    Wh_SetIntValue(L"GlowRange", g_appearance.range); Wh_SetIntValue(L"GlowIntensity", g_appearance.intensity);
    Wh_SetIntValue(L"ButtonRoundClipped", 1); Wh_SetIntValue(L"GlowOverlayNonInteractive", 1);
    Wh_SetIntValue(L"CompactButtonPadding", 2); Wh_SetIntValue(L"ButtonHeight", (int)diameter);
    Wh_SetIntValue(L"ArtworkCircleCropped", 1); Wh_SetIntValue(L"ArtworkSize", (int)iconSize); Wh_SetIntValue(L"ButtonWidth", (int)state.button.Width());
}
// Live color field: fixed sphere silhouette; only the interior palette evolves.
// No network, image decoding per frame, or movement of the taskbar button.
struct FlowBufferAccess : IUnknown {
    virtual HRESULT __stdcall Buffer(BYTE** value) = 0;
};
__CRT_UUID_DECL(FlowBufferAccess, 0x905a0fef, 0xbc53, 0x11df, 0x8c, 0x49, 0x00, 0x1e, 0x4f, 0xc6, 0x86, 0xda)
static void PaintFlow(AttachedButton& state) {
    constexpr int side = 64;
    BYTE* pixels = nullptr;
    winrt::check_hresult(state.flowBitmap.PixelBuffer().as<FlowBufferAccess>()->Buffer(&pixels));
    ULONGLONG now = GetTickCount64();
    state.flowTime += std::min((now - state.flowStarted) / 1000.0, .25) * g_appearance.flowSpeed / 100.0;
    state.flowStarted = now;
    double t = state.flowTime;
    struct ColorSource { double x, y, r, g, b; };
    ColorSource sources[] = {
        {-.48 + .25 * sin(t * .24), -.30 + .25 * cos(t * .19), 255, 159, 202},
        {.22 + .32 * cos(t * .21), -.50 + .23 * sin(t * .27), 255, 240, 143},
        {.48 + .30 * sin(t * .18 + 1), .26 + .33 * cos(t * .25), 143, 87, 249},
        {-.22 + .34 * cos(t * .23 + 2), .55 + .23 * sin(t * .20), 113, 230, 247},
        {-.05 + .35 * sin(t * .16 + 3), -.05 + .36 * cos(t * .22), 240, 167, 248}
    };
    for (int y = 0; y < side; ++y) for (int x = 0; x < side; ++x) {
        double nx = (x + .5) / (side / 2.0) - 1, ny = (y + .5) / (side / 2.0) - 1;
        double radius = nx * nx + ny * ny;
        double alpha = std::clamp((1 - sqrt(radius)) * side / 2.0, 0.0, 1.0);
        double sum = 0, r = 0, g = 0, b = 0;
        for (auto const& source : sources) {
            double dx = nx - source.x, dy = ny - source.y;
            // Smooth moving boundaries keep distinct colors at taskbar sizes.
            double weight = exp(-5.5 * (dx * dx + dy * dy));
            sum += weight; r += weight * source.r; g += weight * source.g; b += weight * source.b;
        }
        double light = .82 + .18 * sqrt(std::max(0.0, 1 - radius));
        double shine = .24 * exp(-7 * ((nx + .32) * (nx + .32) + (ny + .48) * (ny + .48)));
        auto channel = [&](double value) { return (BYTE)(std::clamp(value / sum * light * (1 - shine) + 255 * shine, 0.0, 255.0) * alpha); };
        int offset = (y * side + x) * 4;
        pixels[offset] = channel(b); pixels[offset + 1] = channel(g); pixels[offset + 2] = channel(r); pixels[offset + 3] = (BYTE)(255 * alpha);
    }
    state.flowBitmap.Invalidate();
}
static void SetFlowStatus(AttachedButton& state, PCWSTR status) {
    if (state.flowStatus == status) return;
    state.flowStatus = status;
    bool active = state.flowStatus == L"running";
    state.flowTimer.Interval(std::chrono::milliseconds(active ? 50 : 1000));
    Wh_SetIntValue(L"ColorFlowActive", active ? 1 : 0);
    Wh_SetStringValue(L"ColorFlowStatus", status);
    Wh_Log(L"Taskbar color flow: %s", status);
}

static void RefreshFlow(AttachedButton& state) {
    auto now = GetTickCount64();
    if (now >= state.nextFlowCheck) {
        BOOL enabled = FALSE;
        state.animationPolicyKnown = SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0);
        state.systemAnimations = enabled != FALSE;
        state.nextFlowCheck = now + 1000;
    }
    if (!state.animationPolicyKnown || !state.systemAnimations) {
        if (state.flowBitmap) {
            state.icon.Fill(state.staticFill);
            state.flowBitmap = nullptr;
        }
        SetFlowStatus(state, state.animationPolicyKnown ? L"windows-animations-disabled" : L"animation-policy-unavailable");
        return;
    }
    if (g_appearance.flowSpeed == 0) {
        SetFlowStatus(state, L"paused-by-speed");
        return;
    }
    try {
        if (state.flowStatus != L"running") state.flowStarted = now;
        if (!state.flowBitmap) {
            state.flowBitmap = xaml::Media::Imaging::WriteableBitmap(64, 64);
            PaintFlow(state);
            xaml::Media::ImageBrush fill;
            fill.ImageSource(state.flowBitmap); fill.Stretch(xaml::Media::Stretch::UniformToFill);
            state.icon.Fill(fill);
        } else PaintFlow(state);
        SetFlowStatus(state, L"running");
        if (now >= state.nextFrameReport) {
            SYSTEMTIME utc{};
            GetSystemTime(&utc);
            wchar_t stamp[32];
            swprintf_s(stamp, L"%04u-%02u-%02uT%02u:%02u:%02uZ", utc.wYear, utc.wMonth, utc.wDay,
                utc.wHour, utc.wMinute, utc.wSecond);
            Wh_SetStringValue(L"ColorFlowLastFrameUtc", stamp);
            state.nextFrameReport = now + 5000;
        }
    } catch (...) {
        if (state.flowStatus != L"rendering-failed") LogFailure(L"Draw taskbar color flow");
        state.flowBitmap = nullptr;
        SetFlowStatus(state, L"rendering-failed");
        state.icon.Fill(state.staticFill);
    }
}

static void StartFlow(AttachedButton& state) {
    Wh_SetStringValue(L"ColorFlowLastFrameUtc", L"");
    state.flowTimer = xaml::DispatcherTimer();
    state.flowTick = state.flowTimer.Tick([](auto&&, auto&&) {
        if (g_unloading || !g_attached) return;
        try { RefreshFlow(*g_attached); }
        catch (...) { LogFailure(L"Refresh taskbar color flow"); }
    });
    RefreshFlow(state);
    state.flowTimer.Start();
}

static void EnsureFlowTimer(AttachedButton& state) {
    if (state.flowTimer) return;
    try { StartFlow(state); }
    catch (...) {
        state.flowTimer = nullptr;
        state.flowBitmap = nullptr;
        state.icon.Fill(state.staticFill);
        Wh_SetIntValue(L"ColorFlowActive", 0);
        if (state.flowStatus != L"timer-initialization-failed") LogFailure(L"Initialize taskbar color flow");
        state.flowStatus = L"timer-initialization-failed";
        Wh_SetStringValue(L"ColorFlowStatus", state.flowStatus.c_str());
    }
}
static void PositionGlow(AttachedButton& state) {
    auto buttonPoint = state.button.TransformToVisual(state.parent).TransformPoint({0, 0});
    auto overlayPoint = state.glowOverlay.TransformToVisual(state.parent).TransformPoint({0, 0});
    double baseX = overlayPoint.X - state.glowPosition.X(), baseY = overlayPoint.Y - state.glowPosition.Y();
    double x = buttonPoint.X + (state.button.ActualWidth() - state.glow.Width()) / 2 - baseX;
    double y = buttonPoint.Y + (state.button.ActualHeight() - state.glow.Height()) / 2 - baseY;
    if (std::abs(x - state.glowPosition.X()) > .1) state.glowPosition.X(x);
    if (std::abs(y - state.glowPosition.Y()) > .1) state.glowPosition.Y(y);
    state.glowOverlay.Visibility(xaml::Visibility::Visible);
}
static void ApplyAppearanceState(void* parameter) {
    if (g_unloading) return;
    g_appearance = *static_cast<AppearanceState*>(parameter);
    UpdateButtonAppearance();
}


static bool SignalAiCommand(std::wstring_view command) {
    if (g_commandPrefix.empty() || !g_verifyDropDirectory.empty()) return false;
    PCWSTR action = L".Open";
    if (command.starts_with(L"--toggle-ai")) action = L".Toggle";
    else if (command == L"--settings") action = L".Settings";
    else if (command == L"--screenshot") action = L".Screenshot";
    else if (command == L"--taskbar-drop") action = L".Drop";
    else if (command == L"--drag-hover") action = L".Hover";
    HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, (g_commandPrefix + action).c_str());
    if (!event) return false;
    if (command.starts_with(L"--toggle-ai")) {
        struct Gesture { unsigned long time; long x, y; unsigned long dispatched; } gesture{};
        constexpr std::wstring_view marker = L"--toggle-ai --gesture ";
        if (command.starts_with(marker))
            swscanf(command.data() + marker.size(), L"%lu,%ld,%ld,%lu", &gesture.time, &gesture.x, &gesture.y, &gesture.dispatched);
        HANDLE mapping = OpenFileMappingW(FILE_MAP_WRITE, FALSE, (g_commandPrefix + L".ToggleInput").c_str());
        void* shared = mapping ? MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, sizeof(gesture)) : nullptr;
        if (shared) {
            *static_cast<Gesture*>(shared) = gesture;
            MemoryBarrier();
            UnmapViewOfFile(shared);
        }
        if (mapping) CloseHandle(mapping);
        if (!shared) { CloseHandle(event); return false; }
    }
    bool signaled = SetEvent(event) != FALSE;
    CloseHandle(event);
    return signaled;
}

static void LaunchAiCommand(PCWSTR command) {
    if (g_unloading) return;
    AllowSetForegroundWindow(ASFW_ANY);
    if (SignalAiCommand(command)) return;
    if (GetFileAttributesW(g_executable.c_str()) == INVALID_FILE_ATTRIBUTES) {
        Wh_Log(L"AIEnter executable is missing: %s", g_executable.c_str());
        return;
    }
    SHELLEXECUTEINFOW info{sizeof(info)};
    info.fMask = SEE_MASK_FLAG_NO_UI;
    info.lpFile = g_executable.c_str();
    std::wstring parameters = std::wstring(L"--ai ") + command;
    if (!g_verifyDropDirectory.empty()) parameters += L" --verify-drop-entry --data-dir \"" + g_verifyDropDirectory + L"\"";
    info.lpParameters = parameters.c_str();
    info.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&info)) Wh_Log(L"AI launch failed: %u", GetLastError());
}


static void LaunchAi(bool settings = false) { LaunchAiCommand(settings ? L"--settings" : L"--ai-only"); }
namespace transfer = winrt::Windows::ApplicationModel::DataTransfer;
namespace storage = winrt::Windows::Storage;
namespace json = winrt::Windows::Data::Json;
static std::wstring DropRoot() {
    if (!g_verifyDropDirectory.empty()) {
        auto root = g_verifyDropDirectory + L"\\AIEnter";
        CreateDirectoryW(root.c_str(), nullptr);
        root += L"\\incoming-drops";
        CreateDirectoryW(root.c_str(), nullptr);
        return root;
    }
    PWSTR path = nullptr;
    winrt::check_hresult(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &path));
    std::wstring root(path); CoTaskMemFree(path);
    if (_wcsicmp(g_profileDirectory.c_str(), (root + L"\\AIEnter").c_str()) != 0) {
        root += L"\\AITaskbar"; CreateDirectoryW(root.c_str(), nullptr);
    }
    root += L"\\AIEnter"; CreateDirectoryW(root.c_str(), nullptr);
    root += L"\\incoming-drops"; CreateDirectoryW(root.c_str(), nullptr); return root;
}
static std::string ReadLocalFile(std::wstring const& path) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return {};
    DWORD size = GetFileSize(file, nullptr);
    if (size == INVALID_FILE_SIZE || size > 16 * 1024 * 1024) { CloseHandle(file); return {}; }
    std::string data(size, '\0'); DWORD read = 0; BOOL ok = ReadFile(file, data.data(), size, &read, nullptr); CloseHandle(file);
    if (!ok) return {}; data.resize(read); return data;
}
static winrt::hstring CurrentDropProvider() {
    try {
        auto root = DropRoot(); root.resize(root.size() - std::wstring(L"\\incoming-drops").size());
        auto data = ReadLocalFile(root + L"\\ai.json");
        if (!data.empty()) return json::JsonObject::Parse(winrt::to_hstring(data)).GetNamedString(L"Provider", L"");
    } catch (...) {} return L"";
}
static bool DropSupported(transfer::DataPackageView const& data) {
    return data.Contains(transfer::StandardDataFormats::StorageItems()) || data.Contains(transfer::StandardDataFormats::WebLink())
        || data.Contains(transfer::StandardDataFormats::Text()) || data.Contains(transfer::StandardDataFormats::Bitmap());
}
static void ResetDropVisual() {
    if (!g_attached) return;
    auto& state = *g_attached;
    if (state.hoverTimer) state.hoverTimer.Stop();
    state.button.BorderBrush(state.normalBorder); state.button.BorderThickness(state.normalThickness); state.dragging = false;
}
static void OverTaskbar(xaml::DragEventArgs const& args) {
    if (g_unloading || !g_attached || !DropSupported(args.DataView())) return;
    args.Handled(true); args.AcceptedOperation(transfer::DataPackageOperation::Copy);
    args.DragUIOverride().Caption(L"放入当前 AI，不自动发送"); args.DragUIOverride().IsCaptionVisible(true);
    if (!g_attached->dragging) {
        g_attached->dragging = true; g_attached->hoverTimer.Start();
        g_attached->button.BorderThickness({2, 2, 2, 2});
        g_attached->button.BorderBrush(xaml::Media::SolidColorBrush(winrt::Windows::UI::Color{255, 30, 180, 230}));
    }
}
static void WritePacket(std::wstring const& root, std::wstring const& id, json::JsonObject const& packet) {
    auto text = winrt::to_string(packet.Stringify());
    std::wstring temporary = root + L"\\" + id + L".tmp", final = root + L"\\" + id + L".json";
    HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) winrt::throw_last_error();
    DWORD written = 0; BOOL ok = WriteFile(file, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
    FlushFileBuffers(file); CloseHandle(file);
    if (!ok || written != text.size() || !MoveFileExW(temporary.c_str(), final.c_str(), MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(temporary.c_str()); winrt::throw_last_error();
    }
}
static winrt::fire_and_forget DropOnTaskbar(xaml::DragEventArgs args) {
    if (g_unloading || !DropSupported(args.DataView())) co_return;
    ++g_pendingDrops;
    auto deferral = args.GetDeferral();
    args.Handled(true); args.AcceptedOperation(transfer::DataPackageOperation::Copy); ResetDropVisual();
    std::wstring root, id, batch;
    std::vector<std::wstring> owned;
    auto provider = CurrentDropProvider();
    try {
        root = DropRoot(); GUID guid{}; winrt::check_hresult(CoCreateGuid(&guid)); WCHAR name[40]; StringFromGUID2(guid, name, ARRAYSIZE(name));
        id = std::wstring(name + 1, 36); batch = root + L"\\" + id; CreateDirectoryW(batch.c_str(), nullptr);
        auto folder = co_await storage::StorageFolder::GetFolderFromPathAsync(batch);
        auto data = args.DataView(); json::JsonObject packet; json::JsonArray files, ownedFiles;
        packet.SetNamedValue(L"version", json::JsonValue::CreateNumberValue(1)); packet.SetNamedValue(L"provider", json::JsonValue::CreateStringValue(provider));
        winrt::hstring text;
        bool bitmapOnly = data.Contains(transfer::StandardDataFormats::Bitmap()) && !data.Contains(transfer::StandardDataFormats::StorageItems());
        if (!bitmapOnly && data.Contains(transfer::StandardDataFormats::WebLink())) text = (co_await data.GetWebLinkAsync()).AbsoluteUri();
        else if (!bitmapOnly && data.Contains(transfer::StandardDataFormats::Text())) text = co_await data.GetTextAsync();
        std::wstring value(text);
        bool link = (value.starts_with(L"https://") || value.starts_with(L"http://")) && value.find_first_of(L"\r\n") == std::wstring::npos;
        if (!link && data.Contains(transfer::StandardDataFormats::StorageItems())) {
            text = L""; auto items = co_await data.GetStorageItemsAsync();
            if (items.Size() > 12) throw winrt::hresult_invalid_argument(L"一次最多拖入 12 个文件。");
            for (auto const& item : items) {
                if (!item.IsOfType(storage::StorageItemTypes::File)) throw winrt::hresult_invalid_argument(L"不支持拖入文件夹。");
                auto path = item.Path();
                if (path.empty()) {
                    auto copied = co_await item.as<storage::StorageFile>().CopyAsync(folder, L"附件_" + item.Name(), storage::NameCollisionOption::GenerateUniqueName);
                    path = copied.Path(); owned.emplace_back(path); ownedFiles.Append(json::JsonValue::CreateStringValue(path));
                }
                files.Append(json::JsonValue::CreateStringValue(path));
            }
        } else if (bitmapOnly) {
            auto reference = co_await data.GetBitmapAsync(); auto stream = co_await reference.OpenReadAsync();
            if (stream.Size() > 32 * 1024 * 1024) throw winrt::hresult_invalid_argument(L"图片超过 32 MB。");
            auto reader = winrt::Windows::Storage::Streams::DataReader(stream.GetInputStreamAt(0));
            co_await reader.LoadAsync(static_cast<uint32_t>(stream.Size()));
            std::vector<uint8_t> bytes(static_cast<size_t>(stream.Size())); reader.ReadBytes(bytes);
            auto file = co_await folder.CreateFileAsync(L"image.source", storage::CreationCollisionOption::FailIfExists);
            co_await storage::FileIO::WriteBytesAsync(file, bytes);
            owned.emplace_back(file.Path()); ownedFiles.Append(json::JsonValue::CreateStringValue(file.Path()));
            packet.SetNamedValue(L"bitmap", json::JsonValue::CreateStringValue(file.Path()));
        }
        if (text.size() > 20000) throw winrt::hresult_invalid_argument(L"文字最多 20000 字。");
        packet.SetNamedValue(L"text", json::JsonValue::CreateStringValue(text)); packet.SetNamedValue(L"files", files); packet.SetNamedValue(L"ownedFiles", ownedFiles);
        WritePacket(root, id, packet); Wh_SetIntValue(L"TaskbarDropQueued", Wh_GetIntValue(L"TaskbarDropQueued", 0) + 1);
        LaunchAiCommand(L"--taskbar-drop");
    } catch (...) {
        auto error = winrt::to_hresult(); Wh_SetIntValue(L"TaskbarDropError", error);
        for (auto const& path : owned) DeleteFileW(path.c_str());
        if (!batch.empty()) RemoveDirectoryW(batch.c_str());
        try {
            if (!root.empty() && !id.empty()) {
                json::JsonObject packet;
                packet.SetNamedValue(L"error", json::JsonValue::CreateStringValue(L"无法读取拖入内容，请直接拖入 AI 窗口或先保存文件到本机。"));
                packet.SetNamedValue(L"provider", json::JsonValue::CreateStringValue(provider));
                WritePacket(root, id, packet); LaunchAiCommand(L"--taskbar-drop");
            }
        } catch (...) {}
    }
    deferral.Complete(); --g_pendingDrops;
}

static xaml::FrameworkElement FindElement(xaml::DependencyObject const& root,
                                         wchar_t const* name, unsigned depth = 0) {
    if (!root || depth > 32) return nullptr;
    if (auto element = root.try_as<xaml::FrameworkElement>();
        element && element.Name() == name) return element;
    int count = xaml::Media::VisualTreeHelper::GetChildrenCount(root);
    for (int index = 0; index < count; ++index) {
        auto found = FindElement(xaml::Media::VisualTreeHelper::GetChild(root, index), name, depth + 1);
        if (found) return found;
    }
    return nullptr;
}

static void RemoveButton() {
    if (!g_attached) return;
    auto* state = g_attached;
    g_attached = nullptr;
    // Explorer can disconnect its XAML tree during restart or a display/DPI rebuild.
    // Detach each handler independently so one stale COM reference cannot prevent slot release.
    auto attempt = [](auto&& action) { try { action(); } catch (...) {} };
    if (state->button) {
        attempt([&] { if (state->clickTimer) { state->clickTimer.Stop(); state->clickTimer.Tick(state->clickTick); } });
        attempt([&] { if (state->flowTimer) { state->flowTimer.Stop(); state->flowTimer.Tick(state->flowTick); } });
        attempt([&] { if (state->hoverTimer) { state->hoverTimer.Stop(); state->hoverTimer.Tick(state->hoverTick); } });
        attempt([&] { state->button.DragEnter(state->dragEnter); }); attempt([&] { state->button.DragOver(state->dragOver); });
        attempt([&] { state->button.DragLeave(state->dragLeave); }); attempt([&] { state->button.Drop(state->drop); });
        attempt([&] { state->button.RemoveHandler(xaml::UIElement::PointerPressedEvent(), state->pointerPress); });
        attempt([&] { state->button.Click(state->click); }); attempt([&] { state->button.RightTapped(state->rightTap); });
        attempt([&] { state->button.LayoutUpdated(state->layout); });
        attempt([&] { controls::Primitives::FlyoutBase::SetAttachedFlyout(state->button, nullptr); });
        uint32_t index;
        attempt([&] { if (state->parent.Children().IndexOf(state->glowOverlay, index)) state->parent.Children().RemoveAt(index); });
        attempt([&] { if (state->parent.Children().IndexOf(state->button, index)) state->parent.Children().RemoveAt(index); });
    }
    attempt([&] { slot::Release(state->parent, state->lease); });
    delete state;
    Wh_SetIntValue(L"ButtonAttached", 0); Wh_SetIntValue(L"TaskbarDropEnabled", 0);
    Wh_SetIntValue(L"ArtworkLoaded", 0);
    Wh_SetIntValue(L"ArtworkPixels", 0);
    Wh_SetIntValue(L"ColorFlowActive", 0);
    Wh_SetStringValue(L"ColorFlowStatus", L"detached");
}

static void SendPendingClick(AttachedButton& state) {
    if (!state.clickPending) return;
    state.clickPending = false;
    std::wstring command = L"--toggle-ai --gesture " + std::to_wstring(state.pendingClickTime) + L"," + std::to_wstring(state.pendingClickPoint.x) + L"," + std::to_wstring(state.pendingClickPoint.y) + L"," + std::to_wstring(GetTickCount());
    LaunchAiCommand(command.c_str());
}
static bool IsSecondClick(AttachedButton const& state, DWORD time, POINT point) {
    return state.clickPending && (DWORD)(time - state.pendingClickTime) <= GetDoubleClickTime()
        && std::abs(point.x - state.pendingClickPoint.x) <= GetSystemMetrics(SM_CXDOUBLECLK) / 2
        && std::abs(point.y - state.pendingClickPoint.y) <= GetSystemMetrics(SM_CYDOUBLECLK) / 2;
}
struct AttachRequest {
    HWND window;
    bool attached = false;
};

static void AttachButton(void* parameter) {
    if (g_unloading) return;
    auto& request = *static_cast<AttachRequest*>(parameter);
    auto root = taskbar::GetTaskbarXamlRoot(request.window);
    if (!root) return;
    auto frame = FindElement(root.Content(), L"SystemTrayFrameGrid");
    auto parent = frame ? frame.try_as<controls::Panel>() : nullptr;
    if (!parent) return;
    if (g_attached && g_attached->parent == parent &&
        slot::FindDirectChild(parent, L"AIEnterAiButton")) {
        EnsureFlowTimer(*g_attached);
        request.attached = true;
        return;
    }
    RemoveButton();
    int position;
    if (!slot::ResolveSlot(parent, slot::Anchor::AfterClock, position)) return;
    controls::Button button;
    button.Name(L"AIEnterAiButton");
    button.MinWidth(0); button.MinHeight(0);
    button.Width(28);
    button.Padding({0, 0, 0, 0});
    button.Margin({0, 0, 0, 0}); button.BorderThickness({0, 0, 0, 0});
    button.VerticalAlignment(xaml::VerticalAlignment::Center);
    button.HorizontalContentAlignment(xaml::HorizontalAlignment::Center); button.VerticalContentAlignment(xaml::VerticalAlignment::Center);
    std::wstring asset = g_executable.substr(0, g_executable.find_last_of(L"\\/")) + L"\\Assets\\AIEnter.png";
    xaml::Shapes::Ellipse icon;
    icon.Width(24); icon.Height(24);

    xaml::Media::Imaging::BitmapImage artwork;
    try {
        // Decode the fallback before the animated brush replaces it.
        using winrt::Windows::Storage::Streams::IRandomAccessStream;
        IRandomAccessStream stream{nullptr};
        winrt::check_hresult(CreateRandomAccessStreamOnFile(asset.c_str(), STGM_READ,
            winrt::guid_of<IRandomAccessStream>(), winrt::put_abi(stream)));
        artwork.SetSource(stream);
    } catch (...) { LogFailure(L"Load taskbar artwork"); }
    // The supplied 320px canvas contains a 240px sphere centered at (160,160).
    // Crop in the brush, preserving the source bytes and smooth circular edge.
    xaml::Media::ImageBrush fill;
    fill.ImageSource(artwork);
    fill.Stretch(xaml::Media::Stretch::UniformToFill);
    xaml::Media::ScaleTransform crop;
    crop.CenterX(0.5); crop.CenterY(0.5);
    crop.ScaleX(320.0 / 240.0); crop.ScaleY(320.0 / 240.0);
    fill.RelativeTransform(crop);
    icon.Fill(fill);
    controls::Grid glyphHost, glow;
    glyphHost.IsHitTestVisible(false); glow.IsHitTestVisible(false);
    glyphHost.Children().Append(icon);
    // Zero-size Canvas does not add desired width, and never intercepts input.
    controls::Canvas glowOverlay;
    glowOverlay.Width(0); glowOverlay.Height(0); glowOverlay.IsHitTestVisible(false);
    glowOverlay.HorizontalAlignment(xaml::HorizontalAlignment::Left); glowOverlay.VerticalAlignment(xaml::VerticalAlignment::Top);
    glowOverlay.Visibility(xaml::Visibility::Collapsed); controls::Canvas::SetZIndex(glowOverlay, -1);
    xaml::Media::TranslateTransform glowPosition; glowOverlay.RenderTransform(glowPosition);
    glowOverlay.Children().Append(glow);
    button.Content(glyphHost);
    xaml::Automation::AutomationProperties::SetName(button, L"AIEnter AI 对话");
    controls::ToolTipService::SetToolTip(button, winrt::box_value(L"AIEnter AI · 左键打开／收起对话，右键打开设置"));
    auto state = new AttachedButton;
    state->parent = parent;
    state->button = button; state->glyphHost = glyphHost; state->glow = glow; state->icon = icon;
    state->staticFill = fill;
    state->glowOverlay = glowOverlay; state->glowPosition = glowPosition;
    if (!slot::AcquireAt(parent, position, L"AIEnterAiPosition", state->lease)) { delete state; return; }
    try {
        state->clickTimer = xaml::DispatcherTimer();
        state->clickTick = state->clickTimer.Tick([](auto&&,auto&&) {
            if (!g_attached || g_unloading) return;
            g_attached->clickTimer.Stop(); SendPendingClick(*g_attached);
        });
        state->pointerPress = winrt::box_value(xaml::Input::PointerEventHandler([state](auto&&, auto&& args) {
            if (!args.GetCurrentPoint(state->button).Properties().IsLeftButtonPressed()) return;
            state->pressTime = static_cast<DWORD>(GetMessageTime());
            DWORD position = GetMessagePos(); state->pressPoint = {static_cast<short>(LOWORD(position)), static_cast<short>(HIWORD(position))};
            if (g_appearance.screenshot && IsSecondClick(*state,state->pressTime,state->pressPoint)) {
                state->clickTimer.Stop(); state->clickPending = false;
                state->screenshotAt = GetTickCount64(); LaunchAiCommand(L"--screenshot");
            }
        }));
        button.AddHandler(xaml::UIElement::PointerPressedEvent(), state->pointerPress, true);
        state->click = button.Click([state](auto&&, auto&&) {
            auto time = state->pressTime; auto point = state->pressPoint; state->pressTime = 0;
            ULONGLONG now = GetTickCount64();
            if (state->screenshotAt && now - state->screenshotAt <= GetDoubleClickTime()) return;
            if (!g_appearance.screenshot) {
                state->pendingClickTime = time; state->pendingClickPoint = point; state->clickPending = true;
                SendPendingClick(*state); return;
            }
            if (state->clickPending) { state->clickTimer.Stop(); SendPendingClick(*state); }
            state->pendingClickTime = time; state->pendingClickPoint = point; state->clickPending = true;
            DWORD elapsed = (DWORD)(GetMessageTime() - time);
            state->clickTimer.Interval(std::chrono::milliseconds(std::max(1L,(LONG)GetDoubleClickTime()-(LONG)elapsed)));
            state->clickTimer.Start();
        });
        // A direct right-click action avoids keeping callbacks in an open flyout at unload.
        state->rightTap = button.RightTapped([state](auto&&, auto&& args) { state->clickPending = false; state->clickTimer.Stop(); args.Handled(true); LaunchAi(true); });
        if (!slot::PlaceChild(parent, state->lease, button)) throw winrt::hresult_error(E_FAIL);
        parent.Children().Append(glowOverlay);
    } catch (...) {
        button.RemoveHandler(xaml::UIElement::PointerPressedEvent(), state->pointerPress); button.Click(state->click); button.RightTapped(state->rightTap);
        uint32_t index;
        if (parent.Children().IndexOf(glowOverlay, index)) parent.Children().RemoveAt(index);
        if (parent.Children().IndexOf(button, index)) parent.Children().RemoveAt(index);
        slot::Release(parent, state->lease); delete state; throw;
    }
    g_attached = state;
    state->normalBorder = button.BorderBrush(); state->normalThickness = button.BorderThickness();
    state->hoverTimer = xaml::DispatcherTimer(); state->hoverTimer.Interval(std::chrono::milliseconds(450));
    state->hoverTick = state->hoverTimer.Tick([](auto&&, auto&&) { if (g_attached) g_attached->hoverTimer.Stop(); if (!g_unloading) LaunchAiCommand(L"--drag-hover"); });
    button.AllowDrop(true);
    state->dragEnter = button.DragEnter([](auto&&, xaml::DragEventArgs const& args) { OverTaskbar(args); });
    state->dragOver = button.DragOver([](auto&&, xaml::DragEventArgs const& args) { OverTaskbar(args); });
    state->dragLeave = button.DragLeave([](auto&&, auto&&) { ResetDropVisual(); });
    state->drop = button.Drop([](auto&&, xaml::DragEventArgs const& args) { DropOnTaskbar(args); });
    Wh_SetIntValue(L"TaskbarDropEnabled", 1);
    UpdateButtonAppearance();
    int artworkPixels = artwork.PixelWidth();
    Wh_SetIntValue(L"ArtworkLoaded", artworkPixels > 0 ? 1 : 0);
    Wh_SetIntValue(L"ArtworkPixels", artworkPixels);
    EnsureFlowTimer(*state);
    state->layout = button.LayoutUpdated([state](auto&&, auto&&) {
        try {
            if (state->button.ActualWidth() < 1) return;
            PositionGlow(*state);
            auto clock = slot::FindDirectChild(state->parent, L"NotificationCenterButton");
            if (!clock) return;
            auto buttonPoint = state->button.TransformToVisual(state->parent).TransformPoint({0, 0});
            auto clockPoint = clock.TransformToVisual(state->parent).TransformPoint({0, 0});
            int x = (int)buttonPoint.X, right = (int)(clockPoint.X + clock.ActualWidth()), width = (int)state->button.ActualWidth();
            if (state->lastX == x && state->lastRight == right && state->lastWidth == width) return;
            state->lastX = x; state->lastRight = right; state->lastWidth = width;
            Wh_SetIntValue(L"ButtonWidth", (int)state->button.ActualWidth());
            Wh_SetIntValue(L"ButtonX", (int)buttonPoint.X);
            Wh_SetIntValue(L"ClockX", (int)clockPoint.X);
            double clockRight = clockPoint.X + clock.ActualWidth();
            Wh_SetIntValue(L"ClockRight", (int)clockRight);
            Wh_SetIntValue(L"ButtonAfterClock", clockRight <= buttonPoint.X ? 1 : 0);
            Wh_SetIntValue(L"LayoutDoesNotOverlap", clockRight <= buttonPoint.X ? 1 : 0);
            if (auto desktop = slot::FindDirectChild(state->parent, L"ShowDesktopStack")) {
                auto point = desktop.TransformToVisual(state->parent).TransformPoint({0, 0});
                Wh_SetIntValue(L"ShowDesktopX", (int)point.X);
                Wh_SetIntValue(L"ButtonBeforeDesktop", buttonPoint.X + state->button.ActualWidth() <= point.X ? 1 : 0);
            }
            Wh_SetIntValue(L"ArtworkSize", (int)state->icon.Width());
        } catch (...) { LogFailure(L"Layout validation"); }
    });
    Wh_SetIntValue(L"ButtonAttached", 1);
    request.attached = true;
    Wh_Log(L"Inserted real AI button after clock; slot=%d; layout=%s", position, slot::ClassName(parent).c_str());
}

static void LogFailure(PCWSTR context) {
    try { throw; }
    catch (winrt::hresult_error const& error) { Wh_Log(L"%s: 0x%08X %s", context, (unsigned)error.code().value, error.message().c_str()); }
    catch (...) { Wh_Log(L"%s failed", context); }
}

static void Rebuilt() {
    if (g_refresh && !g_unloading) {
        SetEvent(g_refresh);
    }
}

BOOL Wh_ModInit() {
    // Explorer folder windows can load the mod too. Only the process that
    // attaches the actual taskbar button may publish its status.
    auto executable = Wh_GetStringSetting(L"executable");
    g_executable = executable; Wh_FreeStringSetting(executable);
    auto prefix = Wh_GetStringSetting(L"commandPrefix");
    g_commandPrefix = prefix ? prefix : L""; if (prefix) Wh_FreeStringSetting(prefix);
    if (!g_commandPrefix.starts_with(L"Local\\AITaskbar.AIEnter.")) g_commandPrefix.clear();
    auto profile = Wh_GetStringSetting(L"dataDirectory");
    g_profileDirectory = profile ? profile : L""; if (profile) Wh_FreeStringSetting(profile);
    if (g_executable.empty() || GetFileAttributesW(g_executable.c_str()) == INVALID_FILE_ATTRIBUTES) return FALSE;
    auto verify = Wh_GetStringSetting(L"verifyDataDirectory");
    std::wstring requested = verify ? verify : L""; if (verify) Wh_FreeStringSetting(verify);
    if (!requested.empty()) {
        wchar_t full[32768]; DWORD length = GetFullPathNameW(requested.c_str(), ARRAYSIZE(full), full, nullptr);
        auto parent = g_executable.substr(0, g_executable.find_last_of(L"\\/"));
        parent = parent.substr(0, parent.find_last_of(L"\\/")) + L"\\.build\\";
        DWORD attributes = length && length < ARRAYSIZE(full) ? GetFileAttributesW(full) : INVALID_FILE_ATTRIBUTES;
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT)
            && _wcsnicmp(full, parent.c_str(), parent.size()) == 0) g_verifyDropDirectory = full;
    }

    dispatch::SetExceptionLogger(LogFailure);
    if (!taskbar::HookTaskbarSymbols(Rebuilt)) return FALSE;
    g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_refresh = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    g_appearanceEvent = CreateEventW(nullptr, FALSE, FALSE, L"Local\\AITaskbar.AIEnter.TaskbarAppearanceChanged");
    g_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 40, L"Local\\AITaskbar.AIEnter.TaskbarAppearance.v2");
    if (g_mapping) g_shared = static_cast<volatile LONG*>(MapViewOfFile(g_mapping, FILE_MAP_READ, 0, 0, 40));
    if (!g_stop || !g_refresh || !g_appearanceEvent || !g_mapping || !g_shared) {
        if (g_stop) CloseHandle(g_stop);
        if (g_refresh) CloseHandle(g_refresh);
        if (g_shared) UnmapViewOfFile(const_cast<LONG*>(g_shared));
        if (g_mapping) CloseHandle(g_mapping);
        if (g_appearanceEvent) CloseHandle(g_appearanceEvent);
        g_shared = nullptr; g_mapping = g_appearanceEvent = nullptr;
        g_stop = g_refresh = nullptr;
        return FALSE;
    }
    return TRUE;
}

void Wh_ModAfterInit() {
    g_worker = std::thread([] {
        HANDLE watchedProcess = nullptr;
        LONG watchedPid = 0;
        auto apply = [&](AppearanceState state) {
            if (state.pid != watchedPid || !watchedProcess) {
                if (watchedProcess) CloseHandle(watchedProcess);
                watchedProcess = state.pid ? OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, state.pid) : nullptr;
                watchedPid = state.pid;
                WCHAR path[32768]; DWORD length = ARRAYSIZE(path);
                if (watchedProcess && (!QueryFullProcessImageNameW(watchedProcess, 0, path, &length) || _wcsicmp(path, g_executable.c_str()))) {
                    CloseHandle(watchedProcess); watchedProcess = nullptr;
                }
            }
            if (!watchedProcess || WaitForSingleObject(watchedProcess, 0) == WAIT_OBJECT_0) state.open = 0;
            auto window = folder_menus_taskbar_window::FindCurrentProcessTaskbarWnd();
            if (window) dispatch::RunFromWindowThread(window, ApplyAppearanceState, &state, L"AIEnter.TaskbarAi.Dispatch");
        };
        bool reattach = true;
        while (!g_unloading) {
            apply(ReadAppearance());
            if (reattach) {
                for (int attempt = 0; attempt < 40 && !g_unloading; ++attempt) {
                    AttachRequest request{folder_menus_taskbar_window::FindCurrentProcessTaskbarWnd()};
                    if (request.window && dispatch::RunFromWindowThread(request.window, AttachButton, &request, L"AIEnter.TaskbarAi.Dispatch") && request.attached) break;
                    if (WaitForSingleObject(g_stop, 1500) == WAIT_OBJECT_0) break;
                }
            }
            HANDLE events[]{g_stop, g_refresh, g_appearanceEvent, watchedProcess};
            // Periodically revalidate the XAML target. This also refreshes the real slot after
            // taskbar auto-hide, monitor/DPI changes, or a transient disconnected taskbar tree.
            DWORD signaled = WaitForMultipleObjects(watchedProcess ? 4 : 3, events, FALSE, 2500);
            if (signaled == WAIT_OBJECT_0 || signaled == WAIT_FAILED) break;
            reattach = signaled == WAIT_TIMEOUT || signaled == WAIT_OBJECT_0 + 1;
            if (signaled == WAIT_OBJECT_0 + 3) {
                AppearanceState state = ReadAppearance(); state.open = 0; apply(state);
                if (watchedProcess) CloseHandle(watchedProcess);
                watchedProcess = nullptr; watchedPid = 0;
                reattach = true;
            }
        }
        if (watchedProcess) CloseHandle(watchedProcess);
    });
}

void Wh_ModUninit() {
    g_unloading = true;
    if (g_stop) SetEvent(g_stop);
    if (g_worker.joinable()) g_worker.join();
    while (g_pendingDrops.load() > 0) Sleep(20);
    auto window = folder_menus_taskbar_window::FindCurrentProcessTaskbarWnd();
    if (window) dispatch::RunFromWindowThread(window, [](void*) { RemoveButton(); }, nullptr, L"AIEnter.TaskbarAi.Dispatch");
    if (g_stop) CloseHandle(g_stop);
    if (g_refresh) CloseHandle(g_refresh);
    if (g_shared) UnmapViewOfFile(const_cast<LONG*>(g_shared));
    if (g_mapping) CloseHandle(g_mapping);
    if (g_appearanceEvent) CloseHandle(g_appearanceEvent);
    g_shared = nullptr; g_mapping = g_appearanceEvent = nullptr;
    g_stop = g_refresh = nullptr;
}
