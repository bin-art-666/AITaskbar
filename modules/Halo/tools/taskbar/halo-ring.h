// The Path remains the sole mask: its center and surrounding hit area stay clear.
struct RingBufferAccess : IUnknown { virtual HRESULT __stdcall Buffer(BYTE** value) = 0; };
__CRT_UUID_DECL(RingBufferAccess, 0x905a0fef, 0xbc53, 0x11df, 0x8c, 0x49, 0x00, 0x1e, 0x4f, 0xc6, 0x86, 0xda)
static xaml::Shapes::Path RingPath(Attached& state) {
    return Find(state.button.Content().as<xaml::FrameworkElement>(), L"HaloRingPath").try_as<xaml::Shapes::Path>();
}
static unsigned RingBase(RingState const& value, bool light) { return light ? value.lightColor : value.color; }
static unsigned RingBase(RingState const& value) { return RingBase(value, value.light); }
static unsigned RingAccent(RingState const& value, bool light) {
    if (!light) return 0xFFFFFFFFu;
    unsigned result = 0xFF000000u;
    for (int channel = 0; channel < 3; ++channel) result |= (unsigned)(((value.lightColor >> (channel * 8)) & 255) * .35) << (channel * 8);
    return result;
}
static double RingFlowWeight(double behind, int length) {
    constexpr double tau = 6.283185307179586;
    double t = behind / (tau * length / 100.0); if (t <= 0 || t >= 1) return 0;
    double front = std::clamp(t / .07, 0.0, 1.0); front = front * front * (3 - 2 * front);
    return std::clamp(front * (1 - t) * (1 - t) / .9, 0.0, 1.0);
}
static void StaticRing(Attached& state, bool white) {
    unsigned c = white ? RingAccent(state.ring, state.ring.light) : RingBase(state.ring);
    if (auto path = RingPath(state)) path.Fill(xaml::Media::SolidColorBrush(winrt::Windows::UI::Color{255, (BYTE)(c >> 16), (BYTE)(c >> 8), (BYTE)c}));
}
static bool RingAnimations() {
    BOOL enabled = TRUE; SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0); return enabled != FALSE;
}
static constexpr double RingTurn = 6.283185307179586;
static unsigned ImportPulseAge(unsigned pulse, ULONGLONG now) {
    return ((static_cast<unsigned>(now / 16) - (pulse & 0x7FFFFFFFu)) & 0x7FFFFFFFu);
}
static int ImportVisual(unsigned pulse, ULONGLONG now) {
    if (!pulse) return 0;
    auto age = ImportPulseAge(pulse, now);
    return (pulse & 0x80000000u) ? (age < 750 ? 2 : 0) : (age < 75 ? 1 : 0);
}
static void PaintRingFlow(Attached& state) {
    auto now = GetTickCount64(); double dt = std::min((now - state.flowLast) / 1000.0, .1); state.flowLast = now;
    bool animate = RingAnimations();
    int import = ImportVisual(state.ring.importPulse, now);
    if (import != state.lastImportVisual) {
        state.lastImportVisual = import;
        Wh_SetIntValue(L"RingImportVisual", import);
        if (import) Wh_SetIntValue(L"RingLastImportVisual", import);
    }
    bool hoverFlow = state.ring.flow && animate;
    state.flowOpacity = hoverFlow ? std::clamp(state.flowOpacity + (state.pointerOver ? 1 : -1) * dt / .15, 0.0, 1.0) : 0;
    if (!import && !(hoverFlow && (state.pointerOver || state.flowOpacity > 0))) {
        state.flowTimer.Stop(); StaticRing(state, state.pointerOver); Wh_SetIntValue(L"RingFlowActive", 0); return;
    }
    constexpr int side = 64; constexpr double tau = RingTurn;
    double importAngle = ImportPulseAge(state.ring.importPulse, now) / 75.0 * tau + 3 * tau / 4;
    state.flowAngle = fmod(state.flowAngle + dt * tau / 2.4 * state.ring.speed / 100.0, tau);
    BYTE* pixels = nullptr; winrt::check_hresult(state.flowBitmap.PixelBuffer().as<RingBufferAccess>()->Buffer(&pixels));
    unsigned baseColor = RingBase(state.ring), accent = RingAccent(state.ring, state.ring.light);
    if (!hoverFlow && state.pointerOver) baseColor = accent;
    if (import == 1) accent = state.ring.light ? 0xFF006B61u : 0xFF84F1D3u;
    if (import == 2) baseColor = state.ring.light ? 0xFF943C00u : 0xFFFFB462u;
    for (int y = 0; y < side; ++y) for (int x = 0; x < side; ++x) {
        double angle = atan2(y + .5 - side / 2., x + .5 - side / 2.) + tau;
        double behind = fmod(state.flowAngle - angle + 2 * tau, tau);
        // Narrow soft head and a longer fading clockwise tail. No silhouette rotation.
        double light = state.flowOpacity * RingFlowWeight(behind, state.ring.length);
        if (import == 1)
            light = std::max(light, animate ? RingFlowWeight(fmod(importAngle - angle + 2 * tau, tau), 38) : 1.0);
        int offset = (y * side + x) * 4;
        double alpha = import == 2 ? std::clamp((std::abs(angle - 3 * tau / 4) - .21) / .07, 0.0, 1.0) : 1.0;
        for (int channel = 0; channel < 3; ++channel) {
            int base = (baseColor >> (channel * 8)) & 255, peak = (accent >> (channel * 8)) & 255;
            pixels[offset + channel] = (BYTE)((base + (peak - base) * light) * alpha);
        }
        pixels[offset + 3] = (BYTE)(255 * alpha);
    }
    state.flowBitmap.Invalidate();
}
static void UpdateRingHover(Attached& state) {
    bool animate = RingAnimations();
    bool hover = animate && state.ring.flow && (state.pointerOver || state.flowOpacity > 0);
    int import = ImportVisual(state.ring.importPulse, GetTickCount64());
    if (!hover && !import && !state.lastImportVisual) {
        state.flowTimer.Stop(); state.flowOpacity = 0; StaticRing(state, state.pointerOver); Wh_SetIntValue(L"RingFlowActive", 0); return;
    }
    if (!state.flowBitmap) state.flowBitmap = xaml::Media::Imaging::WriteableBitmap(64, 64);
    xaml::Media::ImageBrush brush; brush.ImageSource(state.flowBitmap); brush.Stretch(xaml::Media::Stretch::Fill);
    if (auto path = RingPath(state)) path.Fill(brush);
    if (!state.flowTimer.IsEnabled()) state.flowLast = GetTickCount64();
    state.flowTimer.Interval(std::chrono::milliseconds(hover || (animate && import == 1) ? 33 : 150));
    state.flowTimer.Start();
    PaintRingFlow(state);
    Wh_SetIntValue(L"RingFlowActive", hover || (animate && import == 1));
}
