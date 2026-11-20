// Drag transport adapted from AIEnter. Halo imports packets locally without sending content to websites.
#pragma once
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
    auto provider = winrt::hstring{};
    try {
        root = DropRoot(); GUID guid{}; winrt::check_hresult(CoCreateGuid(&guid)); WCHAR name[40]; StringFromGUID2(guid, name, ARRAYSIZE(name));
        id = std::wstring(name + 1, 36); batch = root + L"\\" + id; CreateDirectoryW(batch.c_str(), nullptr);
        auto folder = co_await storage::StorageFolder::GetFolderFromPathAsync(batch);
        auto data = args.DataView(); json::JsonObject packet; json::JsonArray files, ownedFiles;
        packet.SetNamedValue(L"version", json::JsonValue::CreateNumberValue(1)); packet.SetNamedValue(L"provider", json::JsonValue::CreateStringValue(provider));
        winrt::hstring text;
        if (data.Contains(transfer::StandardDataFormats::Html()) && !data.Contains(transfer::StandardDataFormats::StorageItems()) && !data.Contains(transfer::StandardDataFormats::Bitmap())) {
            try {
                auto html = co_await data.GetHtmlFormatAsync();
                if (html.size() > 200000) throw winrt::hresult_invalid_argument(L"网页摘录过大，请缩小选区。");
                packet.SetNamedValue(L"html", json::JsonValue::CreateStringValue(html));
                // Older applications must retain an unsupported packet instead of silently losing HTML-only content.
                packet.SetNamedValue(L"version", json::JsonValue::CreateNumberValue(2));
                if (auto source = data.Properties().ContentSourceWebLink()) packet.SetNamedValue(L"sourceUrl", json::JsonValue::CreateStringValue(source.AbsoluteUri()));
            } catch (...) { if (!data.Contains(transfer::StandardDataFormats::Text()) && !data.Contains(transfer::StandardDataFormats::WebLink())) throw; }
        }
        bool bitmapOnly = data.Contains(transfer::StandardDataFormats::Bitmap()) && !data.Contains(transfer::StandardDataFormats::StorageItems());
        if (!bitmapOnly && data.Contains(transfer::StandardDataFormats::WebLink())) { try { text = (co_await data.GetWebLinkAsync()).AbsoluteUri(); } catch (...) {} }
        if (!bitmapOnly && text.empty() && data.Contains(transfer::StandardDataFormats::Text())) { try { text = co_await data.GetTextAsync(); } catch (...) {} }
        std::wstring value(text);
        bool link = (value.starts_with(L"https://") || value.starts_with(L"http://")) && value.find_first_of(L"\r\n") == std::wstring::npos;
        if (!link && data.Contains(transfer::StandardDataFormats::StorageItems())) {
            text = L""; auto items = co_await data.GetStorageItemsAsync();
            if (items.Size() > 2000) throw winrt::hresult_invalid_argument(L"一次最多拖入 2000 项。");
            for (auto const& item : items) {

                auto path = item.Path();
                if (path.empty()) { if (!item.IsOfType(storage::StorageItemTypes::File)) throw winrt::hresult_invalid_argument(L"虚拟文件夹请先保存到本机。");
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
        LaunchCommand(L"--taskbar-drop");
    } catch (...) {
        auto error = winrt::to_hresult(); Wh_SetIntValue(L"TaskbarDropError", error);
        for (auto const& path : owned) DeleteFileW(path.c_str());
        if (!batch.empty()) RemoveDirectoryW(batch.c_str());
        try {
            if (!root.empty() && !id.empty()) {
                json::JsonObject packet;
                packet.SetNamedValue(L"error", json::JsonValue::CreateStringValue(L"无法读取拖入内容，请直接拖入中转站面板或先保存到本机。"));
                packet.SetNamedValue(L"provider", json::JsonValue::CreateStringValue(provider));
                WritePacket(root, id, packet); LaunchCommand(L"--taskbar-drop");
            }
        } catch (...) {}
    }
    deferral.Complete(); --g_pendingDrops;
}
