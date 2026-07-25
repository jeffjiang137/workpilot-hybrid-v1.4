#pragma once

#include <filesystem>
#include <optional>
#include <string>

namespace workpilot {

class Workspace {
public:
    bool set_root(const std::filesystem::path& root, std::string& error);
    std::string list_files(const std::filesystem::path& relative, int max_items, std::string& error) const;
    std::string read_text(const std::filesystem::path& relative, std::string& error) const;
    std::string write_text(const std::filesystem::path& relative, const std::string& content,
                           const std::string& expected_sha256, std::string& error) const;
    std::optional<std::filesystem::path> resolve_existing(const std::filesystem::path& relative,
                                                           std::string& error) const;
    const std::filesystem::path& root() const noexcept { return root_; }
    bool ready() const noexcept { return !root_.empty(); }

private:
    std::optional<std::filesystem::path> resolve(const std::filesystem::path& relative,
                                                  bool for_write, std::string& error) const;
    bool is_safe_existing_path(const std::filesystem::path& path, std::string& error) const;
    std::filesystem::path root_;
};

}  // namespace workpilot
