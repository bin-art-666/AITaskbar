// ==WindhawkMod==
// @id              aitaskbar-halo
// @name            Halo left taskbar entry
// @description     Native left-side taskbar entry for Halo.
// @version         0.4.0
// @author          Halo
// @include         explorer.exe
// @architecture    x86-64
// @compilerOptions -lole32 -loleaut32 -lruntimeobject -lshell32 -luuid -ladvapi32
// ==/WindhawkMod==
// Taskbar helpers adapted from AIEnter and Taskbar Folder Menus 2.1.
// See ThirdParty/TaskbarIntegration-MIT.txt.
// ==WindhawkModSettings==
/*
- reportDirectory: ""
  $name: Local verification report directory
- diagnosticOnly: true
  $name: Inspect layout without inserting the button
- executable: ""
  $name: AITaskbar executable path
- dataDirectory: ""
  $name: Halo data directory
- appearancePrefix: ""
  $name: Halo appearance communication prefix
*/
// ==/WindhawkModSettings==
#include <windows.h>
#include <shellapi.h>
#include <windhawk_utils.h>
#undef GetCurrentTime
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Markup.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <winrt/Windows.UI.Xaml.Media.Imaging.h>
#include <winrt/Windows.ApplicationModel.DataTransfer.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Data.Json.h>
#include <algorithm>
#include <atomic>
#include <thread>
#include <string>
#include <fstream>
#include <memory>
#include <vector>
#include <cmath>
#include <chrono>
#include "taskbar-support.h"
#include "halo-icon.h"

namespace xaml = winrt::Windows::UI::Xaml;
namespace controls = xaml::Controls;
namespace taskbar = folder_menus_taskbar_xaml;
namespace dispatch = folder_menus_dispatch;
namespace slot = folder_menus_slot_lease;
static HANDLE g_stop = nullptr, g_refresh = nullptr;
static std::thread g_worker;
static std::atomic<bool> g_unloading{false};
static std::wstring g_reports;
static std::wstring g_executable, g_dataDirectory, g_commandPrefix;
static std::atomic<int> g_pendingDrops{0};
static std::atomic<bool> g_enabled{true};
static bool g_diagnostic = true;
static HANDLE g_ringEvent = nullptr, g_ringMapping = nullptr;
static volatile LONG* g_ringShared = nullptr;
struct RingState { int size = 30, thickness = 17; unsigned color = 0xFFE6E6E6; bool flow = true; int speed = 100; unsigned lightColor = 0xFF505050; int length = 80; bool light = false; unsigned importPulse = 0; };
static RingState ReadRing() {
    static RingState last;
    for (int i = 0; i < 32 && g_ringShared; ++i) {
        LONG sequence = g_ringShared[3]; if (sequence & 1) continue;
        MemoryBarrier(); LONG magic = g_ringShared[0], size = g_ringShared[1], thickness = g_ringShared[2]; LONG color = g_ringShared[4], flow = g_ringShared[5], speed = g_ringShared[6], lightColor = g_ringShared[7], length = g_ringShared[8]; MemoryBarrier();
        if (sequence != g_ringShared[3]) continue;
        if (magic == 0x48414C33) last = {std::clamp((int)size, 16, 36), std::clamp((int)thickness, 5, 35), (unsigned)color | 0xFF000000u, flow != 0, std::clamp((int)speed, 50, 200), (unsigned)lightColor | 0xFF000000u, std::clamp((int)length, 10, 80)};
        break;
    }
    DWORD light = 0, bytes = sizeof(light);
    RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"SystemUsesLightTheme", RRF_RT_REG_DWORD, nullptr, &light, &bytes);
    last.light = light != 0;
    last.importPulse = g_ringShared ? static_cast<unsigned>(g_ringShared[9]) : 0;
    return last;
}
struct OriginalChild { xaml::FrameworkElement element{nullptr}; int column = 0, span = 1; bool fullWidth = false; };
struct Attached {
    controls::Grid parent{nullptr};
    controls::Button button{nullptr};
    controls::ColumnDefinition baseColumn{nullptr};
    controls::MenuFlyout menu{nullptr};
    controls::MenuFlyoutItem info{nullptr};
    std::vector<OriginalChild> originals;
    slot::Lease lease;
    winrt::event_token click{}, rightTap{}, layout{};
    winrt::event_token dragEnter{}, dragOver{}, dragLeave{}, drop{}, hoverTick{};
    xaml::DispatcherTimer hoverTimer{nullptr};
    xaml::Media::Brush normalBorder{nullptr};
    xaml::Thickness normalThickness{};
    bool dragging = false;
    bool selected = false;
    int lastX = -1, lastWidth = -1, lastStart = -1;
    int ringSize = -1, ringThickness = -1;
    RingState ring;
    bool pointerOver = false;
    double flowAngle = 0, flowOpacity = 0;
    ULONGLONG flowLast = 0;
    int lastImportVisual = 0;
    xaml::DispatcherTimer flowTimer{nullptr};
    xaml::Media::Imaging::WriteableBitmap flowBitmap{nullptr};
    winrt::event_token pointerEnter{}, pointerExit{}, flowTick{};
};
static std::unique_ptr<Attached> g_attached;
static void Remove();
static void LaunchCommand(wchar_t const* action) {
    if (g_unloading || g_executable.empty()) return;
    AllowSetForegroundWindow(ASFW_ANY);
    std::wstring_view commandView(action);
    PCWSTR eventSuffix = commandView == L"--settings" ? L".Settings" : commandView == L"--toggle" ? L".Toggle"
        : commandView == L"--drag-hover" ? L".Hover" : commandView == L"--drag-leave" ? L".Leave"
        : commandView == L"--taskbar-drop" ? L".Drop" : commandView == L"--pin" ? L".Pin"
        : commandView == L"--detach" ? L".Detach" : commandView == L"--quit" ? L".Quit" : L".Open";
    if (!g_commandPrefix.empty()) {
        if (HANDLE signal = OpenEventW(EVENT_MODIFY_STATE, FALSE, (g_commandPrefix + eventSuffix).c_str())) {
            bool signaled = SetEvent(signal) != FALSE;
            CloseHandle(signal);
            if (signaled) { Wh_SetIntValue(L"LaunchError", 0); return; }
        }
    }
    std::wstring command = std::wstring(L"--halo ") + action;
    SHELLEXECUTEINFOW info{sizeof(info)}; info.fMask = SEE_MASK_FLAG_NO_UI; info.lpVerb = L"open";
    info.lpFile = g_executable.c_str(); info.lpParameters = command.c_str(); info.nShow = SW_SHOWNORMAL;
    Wh_SetIntValue(L"LaunchError", ShellExecuteExW(&info) ? 0 : GetLastError());
}
namespace transfer = winrt::Windows::ApplicationModel::DataTransfer;
namespace storage = winrt::Windows::Storage;
namespace json = winrt::Windows::Data::Json;
static std::wstring DropRoot() {
    CreateDirectoryW(g_dataDirectory.c_str(), nullptr);
    auto path = g_dataDirectory + L"\\incoming-drops"; CreateDirectoryW(path.c_str(), nullptr); return path;
}
static bool DropSupported(transfer::DataPackageView const& data) {
    return data.Contains(transfer::StandardDataFormats::StorageItems()) || data.Contains(transfer::StandardDataFormats::WebLink())
        || data.Contains(transfer::StandardDataFormats::Text()) || data.Contains(transfer::StandardDataFormats::Bitmap()) || data.Contains(transfer::StandardDataFormats::Html());
}
static void ResetDropVisual() { if (g_attached) { g_attached->dragging = false; g_attached->hoverTimer.Stop(); } }
static void OverTaskbar(xaml::DragEventArgs const& args) {
    if (!g_attached || g_unloading || !DropSupported(args.DataView())) return;
    args.Handled(true); args.AcceptedOperation(transfer::DataPackageOperation::Copy);
    args.DragUIOverride().Caption(L"存入 Halo 中转站"); args.DragUIOverride().IsCaptionVisible(true);
    if (!g_attached->dragging) { g_attached->dragging = true; g_attached->hoverTimer.Start(); }
}
#include "halo-drop.h"

static void LogFailure(PCWSTR context) {
    try { throw; }
    catch (winrt::hresult_error const& e) { Wh_Log(L"%s: 0x%08X %s", context, (unsigned)e.code().value, e.message().c_str()); }
    catch (...) { Wh_Log(L"%s failed", context); }
}
static void DumpTree(xaml::DependencyObject const& root, std::ofstream& output, unsigned depth = 0) {
    if (!root || depth > 32) return;
    if (auto element = root.try_as<xaml::FrameworkElement>()) {
        output << std::string(depth * 2, ' ') << winrt::to_string(winrt::get_class_name(element))
            << " #" << winrt::to_string(element.Name()) << " width=" << element.ActualWidth()
            << " height=" << element.ActualHeight() << " column=" << controls::Grid::GetColumn(element)
            << " span=" << controls::Grid::GetColumnSpan(element);
        try { auto p = element.TransformToVisual(nullptr).TransformPoint({0, 0}); output << " x=" << p.X << " y=" << p.Y; } catch (...) {}
        if (auto grid = element.try_as<controls::Grid>()) output << " columns=" << grid.ColumnDefinitions().Size();
        output << "\n";
    }
    int count = xaml::Media::VisualTreeHelper::GetChildrenCount(root);
    for (int i = 0; i < count; ++i) DumpTree(xaml::Media::VisualTreeHelper::GetChild(root, i), output, depth + 1);
}
static xaml::FrameworkElement Find(xaml::DependencyObject const& root, wchar_t const* name, unsigned depth = 0) {
    if (!root || depth > 32) return nullptr;
    if (auto e = root.try_as<xaml::FrameworkElement>(); e && e.Name() == name) return e;
    int count = xaml::Media::VisualTreeHelper::GetChildrenCount(root);
    for (int i = 0; i < count; ++i) if (auto found = Find(xaml::Media::VisualTreeHelper::GetChild(root, i), name, depth + 1)) return found;
    return nullptr;
}
static void SaveTree(xaml::XamlRoot const& root) {
    if (g_reports.empty()) return;
    std::ofstream report((g_reports + L"\\taskbar-tree.txt").c_str());
    DumpTree(root.Content(), report);
}
#include "halo-ring.h"
static void ApplyRing(Attached& state) {
    auto value = ReadRing();
    if (state.ringSize == value.size && state.ringThickness == value.thickness && state.ring.color == value.color && state.ring.flow == value.flow && state.ring.speed == value.speed && state.ring.lightColor == value.lightColor && state.ring.length == value.length && state.ring.light == value.light) {
        if (state.ring.importPulse != value.importPulse) { state.ring.importPulse = value.importPulse; UpdateRingHover(state); }
        return;
    }
    auto artwork = state.button.Content().try_as<xaml::FrameworkElement>();
    auto path = artwork ? Find(artwork, L"HaloRingPath").try_as<xaml::Shapes::Path>() : nullptr;
    if (!path) return; // Viewbox's visual children become available after its first layout.
    constexpr double diameter = 1021.804716, radius = diameter / 2;
    xaml::Media::EllipseGeometry outside, inside;
    outside.Center({512.798285f, 511.301501f}); outside.RadiusX(radius); outside.RadiusY(radius);
    inside.Center(outside.Center()); double inner = radius - diameter * value.thickness / 100.0;
    inside.RadiusX(inner); inside.RadiusY(inner);
    xaml::Media::GeometryGroup ring; ring.FillRule(xaml::Media::FillRule::EvenOdd);
    ring.Children().Append(outside); ring.Children().Append(inside); path.Data(ring);
    artwork.Width(value.size); artwork.Height(value.size);
    if (auto focus = Find(state.button, L"KeyboardFocusRing")) { focus.Width(value.size + 4); focus.Height(value.size + 4); }
    state.ringSize = value.size; state.ringThickness = value.thickness; state.ring = value;
    UpdateRingHover(state);
    Wh_SetIntValue(L"RingBaseColor", (int)RingBase(value));
    Wh_SetIntValue(L"RingTaskbarLight", value.light); Wh_SetIntValue(L"RingFlowLength", value.length);
    Wh_SetIntValue(L"RingPaletteVerified", RingBase(value, true) == value.lightColor && RingBase(value, false) == value.color && RingAccent(value, false) == 0xFFFFFFFFu && RingFlowWeight(0.0, 35) == 0 && RingFlowWeight(6.0, 80) == 0); Wh_SetIntValue(L"RingHoverFlowEnabled", value.flow); Wh_SetIntValue(L"RingFlowSpeed", value.speed);
    Wh_SetIntValue(L"RingSize", value.size); Wh_SetIntValue(L"RingThicknessPercent", value.thickness);
    Wh_SetIntValue(L"RingHolePreserved", inner > 0);
    Wh_SetIntValue(L"RingLiveAppearance", g_ringShared && g_ringEvent);
}
static bool Intersects(xaml::FrameworkElement const& a, xaml::FrameworkElement const& b) {
    if (!a || !b || b.Visibility() != xaml::Visibility::Visible || b.ActualWidth() < 1 || b.ActualHeight() < 1) return false;
    auto x = a.TransformToVisual(nullptr).TransformPoint({0, 0});
    auto y = b.TransformToVisual(nullptr).TransformPoint({0, 0});
    return x.X < y.X + b.ActualWidth() - .5 && y.X < x.X + a.ActualWidth() - .5
        && x.Y < y.Y + b.ActualHeight() - .5 && y.Y < x.Y + a.ActualHeight() - .5;
}
static void Toggle(Attached& state) {
    Wh_SetIntValue(L"ClickCount", Wh_GetIntValue(L"ClickCount", 0) + 1);
    LaunchCommand(L"--toggle");
}
static void Validate(Attached& state) {
    ApplyRing(state);
    if (state.button.ActualWidth() < 1) return;
    auto root = state.button.XamlRoot(); if (!root) return;
    auto point = state.button.TransformToVisual(nullptr).TransformPoint({0, 0});
    auto start = Find(root.Content(), L"LaunchListButton");
    int startX = start ? (int)std::lround(start.TransformToVisual(nullptr).TransformPoint({0, 0}).X) : -1;
    int x = (int)std::lround(point.X), width = (int)std::lround(state.button.ActualWidth());
    if (state.lastX == x && state.lastWidth == width && state.lastStart == startX) return;
    state.lastX = x; state.lastWidth = width; state.lastStart = startX;
    auto widget = Find(root.Content(), L"AugmentedEntryPointButton");
    auto tray = Find(root.Content(), L"SystemTrayFrameGrid");
    bool clear = !Intersects(state.button, start) && !Intersects(state.button, widget) && !Intersects(state.button, tray);
    uint32_t markerIndex = 0;
    bool leased = slot::FindMarker(state.parent, state.lease, markerIndex);
    bool hit = false;
    winrt::Windows::Foundation::Point center{point.X + (float)state.button.ActualWidth() / 2, point.Y + (float)state.button.ActualHeight() / 2};
    for (auto const& element : xaml::Media::VisualTreeHelper::FindElementsInHostCoordinates(center, root.Content().as<xaml::UIElement>())) {
        if (auto target = element.try_as<controls::Button>()) { hit = target == state.button; break; }
    }
    Wh_SetIntValue(L"NativeHitTestPassed", hit);
    auto icon = state.button.Content().try_as<xaml::FrameworkElement>();
    auto rendered = icon ? Find(icon, L"HaloRingPath").try_as<xaml::Shapes::Path>() : nullptr;
    Wh_SetIntValue(L"VectorArtworkHasSize", rendered && rendered.ActualWidth() > 0 && rendered.ActualHeight() > 0 && rendered.Fill());
    Wh_SetIntValue(L"ButtonX", x); Wh_SetIntValue(L"ButtonWidth", width);
    Wh_SetIntValue(L"ButtonHeight", (int)std::lround(state.button.ActualHeight()));
    Wh_SetIntValue(L"StartX", startX);
    Wh_SetIntValue(L"ButtonAtLeft", x >= 0 && x <= 1);
    Wh_SetIntValue(L"LayoutDoesNotOverlap", clear);
    Wh_SetIntValue(L"NativeSlotReserved", leased && controls::Grid::GetColumn(state.button) == 0 && state.parent.ColumnDefinitions().Size() == 2);
    Wh_SetIntValue(L"WidgetPresent", widget && widget.ActualWidth() > 0);
    auto ai = Find(root.Content(), L"AIEnterAiButton");
    Wh_SetIntValue(L"AIEnterPresent", ai && ai.ActualWidth() > 0);
    Wh_SetIntValue(L"AIEnterDoesNotOverlap", ai && !Intersects(state.button, ai));
    Wh_SetIntValue(L"ScalePercent", (int)std::lround(root.RasterizationScale() * 100));
    if (!g_reports.empty()) {
        std::ofstream report((g_reports + L"\\layout.json").c_str());
        report << "{\"buttonX\":" << point.X << ",\"buttonWidth\":" << state.button.ActualWidth()
            << ",\"buttonHeight\":" << state.button.ActualHeight() << ",\"startX\":" << startX
            << ",\"atLeft\":" << (x >= 0 && x <= 1 ? "true" : "false")
            << ",\"noOverlap\":" << (clear ? "true" : "false")
            << ",\"reservedColumn\":" << (leased && state.parent.ColumnDefinitions().Size() == 2 ? "true" : "false")
            << ",\"aiEnterPresent\":" << (ai && ai.ActualWidth() > 0 ? "true" : "false")
            << ",\"rasterizationScale\":" << root.RasterizationScale() << "}";
    }
    SaveTree(root);
}
static void Remove() {
    if (!g_attached) return;
    auto state = std::move(g_attached);
    bool cleanupOk = true;
    auto attempt = [&](auto&& action) { try { action(); } catch (...) { cleanupOk = false; LogFailure(L"Halo cleanup"); } };
    attempt([&] { state->flowTimer.Stop(); state->flowTimer.Tick(state->flowTick); Wh_SetIntValue(L"RingFlowActive", 0); Wh_SetIntValue(L"RingImportVisual", 0); });
    attempt([&] { state->button.PointerEntered(state->pointerEnter); state->button.PointerExited(state->pointerExit); });
    attempt([&] { state->button.Click(state->click); });
    attempt([&] { state->button.RightTapped(state->rightTap); });
    attempt([&] { state->button.LayoutUpdated(state->layout); });
    attempt([&] { state->hoverTimer.Stop(); state->hoverTimer.Tick(state->hoverTick); });
    attempt([&] { state->button.DragEnter(state->dragEnter); state->button.DragOver(state->dragOver); state->button.DragLeave(state->dragLeave); state->button.Drop(state->drop); });
    attempt([&] { state->menu.Hide(); controls::Primitives::FlyoutBase::SetAttachedFlyout(state->button, nullptr); });
    // After a taskbar rebuild the old RootGrid is gone while the button object may still be
    // referenced here. Detect that up front so cleanup never touches a dead tree: touching it
    // throws, skips layout restoration, and misreports OriginalLayoutRestored.
    bool treeAlive = false;
    try { treeAlive = state->parent && state->parent.XamlRoot() != nullptr; } catch (...) { treeAlive = false; }
    if (!treeAlive) {
        Wh_SetIntValue(L"ButtonAttached", 0); Wh_SetIntValue(L"NativeSlotReserved", 0); Wh_SetIntValue(L"TaskbarDropEnabled", 0);
        return;
    }
    attempt([&] { uint32_t i; if (state->parent.Children().IndexOf(state->button, i)) state->parent.Children().RemoveAt(i); });
    // Put spanning decorations back into the content column before releasing our slot.
    for (auto const& child : state->originals) if (child.fullWidth) attempt([&] {
        if (controls::Grid::GetColumn(child.element) == 0 && controls::Grid::GetColumnSpan(child.element) == 2) {
            controls::Grid::SetColumn(child.element, 1); controls::Grid::SetColumnSpan(child.element, child.span);
        }
    });
    attempt([&] { slot::Release(state->parent, state->lease); });
    attempt([&] {
        if (state->parent.ColumnDefinitions().Size() == 1 && state->parent.ColumnDefinitions().GetAt(0) == state->baseColumn)
            state->parent.ColumnDefinitions().Clear();
    });
    attempt([&] { state->parent.UpdateLayout(); });
    bool restored = cleanupOk;
    for (auto const& child : state->originals) attempt([&] {
        if (controls::Grid::GetColumn(child.element) != child.column || controls::Grid::GetColumnSpan(child.element) != child.span) restored = false;
    });
    Wh_SetIntValue(L"OriginalLayoutRestored", restored && state->parent.ColumnDefinitions().Size() == 0);
    Wh_SetIntValue(L"ButtonAttached", 0); Wh_SetIntValue(L"NativeSlotReserved", 0); Wh_SetIntValue(L"TaskbarDropEnabled", 0);
    attempt([&] { if (auto root = state->parent.XamlRoot()) SaveTree(root); });
}
static void Attach(void* parameter) {
    if (g_unloading || !g_enabled) return;
    auto root = taskbar::GetTaskbarXamlRoot(static_cast<HWND>(parameter));
    if (!root) return;
    Wh_SetIntValue(L"XamlRootFound", 1);
    if (g_diagnostic) { static bool saved = false; if (!saved) { SaveTree(root); saved = true; } return; }
    auto frame = Find(root.Content(), L"TaskbarFrame");
    auto parent = frame ? Find(frame, L"RootGrid").try_as<controls::Grid>() : nullptr;
    if (!parent) return;
    if (g_attached && g_attached->parent == parent && slot::FindDirectChild(parent, L"HaloTaskbarButton")) { Validate(*g_attached); return; }
    Remove();
    // Supported contract: the stock RootGrid has one implicit column, a repeater,
    // and full-width decorations. Refuse unknown column layouts instead of overlaying them.
    if (parent.ColumnDefinitions().Size() != 0 || !slot::FindDirectChild(parent, L"TaskbarFrameRepeater")) {
        Wh_SetIntValue(L"UnsupportedLayout", 1); SaveTree(root); return;
    }
    auto state = std::make_unique<Attached>(); state->parent = parent;
    for (auto const& child : parent.Children()) if (auto e = child.try_as<xaml::FrameworkElement>()) {
        if (controls::Grid::GetColumn(e) != 0 || controls::Grid::GetColumnSpan(e) != 1) { Wh_SetIntValue(L"UnsupportedLayout", 1); return; }
        bool full = e.Name() == L"BackgroundControl" || e.Name() == L"ScreenEdgeStroke" || e.Name() == L"GripperControl";
        state->originals.push_back({e, 0, 1, full});
    }
    // Build the button before changing Explorer's layout so resource failures are harmless.
    state->button = controls::Button();
    auto button = state->button;
    button.Name(L"HaloTaskbarButton"); button.Width(44); button.MinWidth(0); button.MinHeight(0);
    button.Padding({0, 0, 0, 0}); button.BorderThickness({0, 0, 0, 0}); button.Margin({0, 0, 0, 0});
    button.HorizontalAlignment(xaml::HorizontalAlignment::Stretch);
    button.VerticalAlignment(xaml::VerticalAlignment::Stretch);
    button.HorizontalContentAlignment(xaml::HorizontalAlignment::Center); button.VerticalContentAlignment(xaml::VerticalAlignment::Center);
    // Keep a full native hit target, but paint only the ring in every pointer state.
    // Explorer's default Button template otherwise adds a rectangular hover/pressed plate.
    button.Background(xaml::Media::SolidColorBrush(winrt::Windows::UI::Color{0, 0, 0, 0}));
    button.UseSystemFocusVisuals(false);
    button.Template(xaml::Markup::XamlReader::Load(LR"xaml(
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
 <Grid x:Name="HaloRingHitArea" Background="Transparent">
  <VisualStateManager.VisualStateGroups>
   <VisualStateGroup x:Name="CommonStates">
    <VisualState x:Name="Normal"/>
    <VisualState x:Name="PointerOver"/>
    <VisualState x:Name="Pressed"><Storyboard><DoubleAnimation Storyboard.TargetName="RingContent" Storyboard.TargetProperty="Opacity" To="0.5" Duration="0"/></Storyboard></VisualState>
    <VisualState x:Name="Disabled"><Storyboard><DoubleAnimation Storyboard.TargetName="RingContent" Storyboard.TargetProperty="Opacity" To="0.3" Duration="0"/></Storyboard></VisualState>
   </VisualStateGroup>
   <VisualStateGroup x:Name="FocusStates">
    <VisualState x:Name="Unfocused"/><VisualState x:Name="PointerFocused"/>
    <VisualState x:Name="Focused"><Storyboard><DoubleAnimation Storyboard.TargetName="KeyboardFocusRing" Storyboard.TargetProperty="Opacity" To="1" Duration="0"/></Storyboard></VisualState>
   </VisualStateGroup>
  </VisualStateManager.VisualStateGroups>
  <Ellipse x:Name="KeyboardFocusRing" Width="28" Height="28" Stroke="{ThemeResource SystemControlHighlightAccentBrush}" StrokeThickness="1" Opacity="0" IsHitTestVisible="False"/>
  <ContentPresenter x:Name="RingContent" Content="{TemplateBinding Content}" HorizontalAlignment="Center" VerticalAlignment="Center" IsHitTestVisible="False"/>
 </Grid>
</ControlTemplate>)xaml").as<controls::ControlTemplate>());
    button.Content(xaml::Markup::XamlReader::Load(HaloIconXaml));
    xaml::Automation::AutomationProperties::SetName(button, L"Halo 中转站入口");
    controls::ToolTipService::SetToolTip(button, winrt::box_value(L"Halo · 中转站（支持拖入）"));
    state->menu = controls::MenuFlyout();
    auto action = [&](wchar_t const* text, wchar_t const* command) {
        controls::MenuFlyoutItem item; item.Text(text); std::wstring value(command);
        item.Click([value](auto&&, auto&&) { LaunchCommand(value.c_str()); }); state->menu.Items().Append(item);
    };
    action(L"设置", L"--settings");
    controls::MenuFlyoutItem quit; quit.Text(L"退出 AITaskbar");
    quit.Click([](auto&&, auto&&) { LaunchCommand(L"--quit"); }); state->menu.Items().Append(quit);
    controls::Primitives::FlyoutBase::SetAttachedFlyout(button, state->menu);
    state->click = button.Click([](auto&&, auto&&) { if (!g_unloading && g_attached) Toggle(*g_attached); });
    state->rightTap = button.RightTapped([](auto&&, xaml::Input::RightTappedRoutedEventArgs const& e) {
        if (!g_unloading && g_attached) {
            e.Handled(true); controls::Primitives::FlyoutBase::ShowAttachedFlyout(g_attached->button);
            Wh_SetIntValue(L"RightClickCount", Wh_GetIntValue(L"RightClickCount", 0) + 1);
        }
    });
    state->flowTimer = xaml::DispatcherTimer(); state->flowTimer.Interval(std::chrono::milliseconds(33));
    state->flowTick = state->flowTimer.Tick([](auto&&, auto&&) {
        if (g_unloading || !g_attached) return;
        try { PaintRingFlow(*g_attached); } catch (...) { g_attached->flowTimer.Stop(); Wh_SetIntValue(L"RingFlowActive", 0); LogFailure(L"Ring flow"); }
    });
    auto hover = [](bool entered) { if (!g_unloading && g_attached) { try { g_attached->pointerOver = entered; UpdateRingHover(*g_attached); } catch (...) { LogFailure(L"Ring hover"); } } };
    state->pointerEnter = button.PointerEntered([hover](auto&&, auto&&) { hover(true); });
    state->pointerExit = button.PointerExited([hover](auto&&, auto&&) { hover(false); });
    state->hoverTimer = xaml::DispatcherTimer(); state->hoverTimer.Interval(std::chrono::milliseconds(350));
    state->hoverTick = state->hoverTimer.Tick([](auto&&, auto&&) { if (g_attached) { g_attached->hoverTimer.Stop(); LaunchCommand(L"--drag-hover"); } });
    button.AllowDrop(true);
    state->dragEnter = button.DragEnter([](auto&&, auto&& e) { OverTaskbar(e); });
    state->dragOver = button.DragOver([](auto&&, auto&& e) { OverTaskbar(e); });
    state->dragLeave = button.DragLeave([](auto&&, auto&&) { ResetDropVisual(); LaunchCommand(L"--drag-leave"); });
    state->drop = button.Drop([](auto&&, auto&& e) { DropOnTaskbar(e); });
    state->baseColumn = controls::ColumnDefinition(); state->baseColumn.Width({1, xaml::GridUnitType::Star});
    parent.ColumnDefinitions().Append(state->baseColumn);
    g_attached = std::move(state);
    try {
        if (!slot::AcquireAt(parent, 0, L"HaloTaskbarPosition", g_attached->lease)) { Remove(); return; }
        for (auto const& child : g_attached->originals) if (child.fullWidth) {
            controls::Grid::SetColumn(child.element, 0); controls::Grid::SetColumnSpan(child.element, 2);
        }
        if (!slot::PlaceChild(parent, g_attached->lease, button)) { Remove(); return; }
        g_attached->layout = button.LayoutUpdated([](auto&&, auto&&) { try { if (g_attached) Validate(*g_attached); } catch (...) { LogFailure(L"Layout validation"); } });
        Wh_SetIntValue(L"ButtonAttached", 1); Wh_SetIntValue(L"UnsupportedLayout", 0);
        Wh_SetIntValue(L"TaskbarDropEnabled", 1);
        Wh_SetIntValue(L"RingImportFeedbackSupported", 1);
        Wh_SetIntValue(L"RingOnlyTemplate", 1); Wh_SetIntValue(L"TightRingCanvas", 1);
        Wh_SetIntValue(L"OriginalLayoutRestored", 0); Wh_SetIntValue(L"VectorArtworkLoaded", 1);
        Wh_SetIntValue(L"Selected", 0);
        Wh_Log(L"Halo attached in native left layout column; AIEnter remains in the tray tree.");
    } catch (...) { Remove(); throw; }
}
static void Rebuilt() { if (g_refresh && !g_unloading) SetEvent(g_refresh); }
BOOL Wh_ModInit() {
    Wh_SetIntValue(L"ButtonAttached", 0); Wh_SetIntValue(L"XamlRootFound", 0);
    auto report = Wh_GetStringSetting(L"reportDirectory");
    g_reports = report ? report : L""; if (report) Wh_FreeStringSetting(report);
    g_diagnostic = Wh_GetIntSetting(L"diagnosticOnly") != 0;
    auto executable = Wh_GetStringSetting(L"executable"); g_executable = executable ? executable : L""; if (executable) Wh_FreeStringSetting(executable);
    auto directory = Wh_GetStringSetting(L"dataDirectory"); g_dataDirectory = directory ? directory : L""; if (directory) Wh_FreeStringSetting(directory);
    if (!g_diagnostic && (g_executable.empty() || g_dataDirectory.empty() || g_dataDirectory.find(L'"') != std::wstring::npos || GetFileAttributesW(g_executable.c_str()) == INVALID_FILE_ATTRIBUTES)) return FALSE;
    dispatch::SetExceptionLogger(LogFailure);
    if (!taskbar::HookTaskbarSymbols(Rebuilt)) return FALSE;
    g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_refresh = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    auto prefixValue = Wh_GetStringSetting(L"appearancePrefix"); std::wstring prefix = prefixValue ? prefixValue : L""; if (prefixValue) Wh_FreeStringSetting(prefixValue);
    g_commandPrefix = prefix.starts_with(L"Local\\AITaskbar.Halo.") ? prefix : L"";
    g_ringEvent = CreateEventW(nullptr, FALSE, FALSE, prefix.empty() ? nullptr : (prefix + L".RingChanged.v3").c_str());
    if (!prefix.empty() && prefix.starts_with(L"Local\\AITaskbar.Halo.")) {
        g_ringMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 40, (prefix + L".RingAppearance.v3").c_str());
        if (g_ringMapping) g_ringShared = static_cast<volatile LONG*>(MapViewOfFile(g_ringMapping, FILE_MAP_READ, 0, 0, 40));
    }
    if (!g_stop || !g_refresh || !g_ringEvent || (!g_diagnostic && !g_ringShared)) {
        if (g_stop) CloseHandle(g_stop); if (g_refresh) CloseHandle(g_refresh); if (g_ringEvent) CloseHandle(g_ringEvent);
        if (g_ringShared) UnmapViewOfFile(const_cast<LONG*>(g_ringShared)); if (g_ringMapping) CloseHandle(g_ringMapping);
        g_stop = g_refresh = g_ringEvent = g_ringMapping = nullptr; g_ringShared = nullptr; return FALSE;
    }
    return TRUE;
}
void Wh_ModAfterInit() {
    g_worker = std::thread([] {
        HKEY themeKey = nullptr; HANDLE themeEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr); bool pending = false;
        RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", 0, KEY_NOTIFY, &themeKey);
        while (!g_unloading) {
            if (themeKey && themeEvent && !pending) pending = RegNotifyChangeKeyValue(themeKey, FALSE, REG_NOTIFY_CHANGE_LAST_SET, themeEvent, TRUE) == ERROR_SUCCESS;
            auto window = folder_menus_taskbar_window::FindCurrentProcessTaskbarWnd();
            if (window) dispatch::RunFromWindowThread(window, Attach, window, L"Halo.Taskbar.Dispatch");
            HANDLE events[4]{g_stop, g_refresh};
            DWORD count = 2;
            // Folder-only Explorer processes must not consume the taskbar's auto-reset notification.
            if (window) events[count++] = g_ringEvent;
            DWORD themeIndex = count;
            if (pending) events[count++] = themeEvent;
            DWORD result = WaitForMultipleObjects(count, events, FALSE, 2500);
            if (pending && result == WAIT_OBJECT_0 + themeIndex) pending = false;
            if (result == WAIT_OBJECT_0 || result == WAIT_FAILED) break;
        }
        if (themeKey) RegCloseKey(themeKey); if (themeEvent) CloseHandle(themeEvent);
    });
}
void Wh_ModUninit() {
    g_unloading = true; if (g_stop) SetEvent(g_stop);
    if (g_worker.joinable()) g_worker.join();
    while (g_pendingDrops.load() > 0) Sleep(20);
    auto window = folder_menus_taskbar_window::FindCurrentProcessTaskbarWnd();
    if (window) dispatch::RunFromWindowThread(window, [](void*) { Remove(); }, nullptr, L"Halo.Taskbar.Dispatch");
    if (g_stop) CloseHandle(g_stop); if (g_refresh) CloseHandle(g_refresh);
    if (g_ringShared) UnmapViewOfFile(const_cast<LONG*>(g_ringShared)); if (g_ringMapping) CloseHandle(g_ringMapping); if (g_ringEvent) CloseHandle(g_ringEvent);
    g_ringShared = nullptr; g_ringMapping = g_ringEvent = nullptr;
    g_stop = g_refresh = nullptr;
}
