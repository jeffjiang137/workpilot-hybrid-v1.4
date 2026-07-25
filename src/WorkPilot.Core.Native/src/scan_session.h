#pragma once

#include "workspace.h"

#include <Windows.h>

#include <atomic>
#include <deque>
#include <filesystem>
#include <string>
#include <vector>

namespace workpilot {

struct ScanOptions {
    bool include_hidden = false;
    int max_depth = 32;
    int max_files = 100000;
    std::vector<std::wstring> ignore_rules;
};

bool parse_scan_options(const std::string& json, ScanOptions& options, std::string& error);
std::string quick_fingerprint(const Workspace& workspace, const std::filesystem::path& relative,
                              std::string& error);

class ScanSession {
public:
    ScanSession(const Workspace& workspace, ScanOptions options);
    std::string next(int max_items, std::string& error);
    void cancel() noexcept { cancelled_.store(true); }

private:
    struct DirectoryWork { std::filesystem::path absolute; std::filesystem::path relative; int depth; };
    struct Entry { std::filesystem::path absolute; std::filesystem::path relative; DWORD attributes; bool directory; };
    bool load_directory(std::string& error);
    bool ignored(const std::filesystem::path& relative, bool directory) const;
    const Workspace& workspace_;
    ScanOptions options_;
    std::deque<DirectoryWork> directories_;
    std::vector<Entry> entries_;
    size_t entry_index_ = 0;
    std::atomic_bool cancelled_{false};
    bool done_ = false;
    bool limit_reached_ = false;
    int directories_seen_ = 0;
    int files_seen_ = 0;
};

}  // namespace workpilot
