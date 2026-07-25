#include "workspace.h"

#include "permission_policy.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <system_error>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

namespace workpilot {
namespace {

std::string json_escape(const std::string& input) {
    std::ostringstream out;
    for (unsigned char c : input) {
        switch (c) {
            case '\\': out << "\\\\"; break;
            case '"': out << "\\\""; break;
            case '\n': out << "\\n"; break;
            case '\r': out << "\\r"; break;
            case '\t': out << "\\t"; break;
            default:
                if (c < 0x20) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << int(c);
                else out << c;
        }
    }
    return out.str();
}

std::string utf8(const std::filesystem::path& path) {
    const auto value = path.generic_wstring();
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                                         static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) return {};
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
                        result.data(), size, nullptr, nullptr);
    return result;
}

bool is_valid_utf8(const std::string& value) {
    if (value.empty()) return true;
    return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0) > 0;
}

std::string sha256(const std::string& bytes) {
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_HASH_HANDLE hash{};
    DWORD object_size{};
    DWORD result_size{};
    std::vector<unsigned char> object;
    std::vector<unsigned char> digest(32);
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) return {};
    auto cleanup = [&] { if (hash) BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); };
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size),
                          sizeof(object_size), &result_size, 0) != 0) { cleanup(); return {}; }
    object.resize(object_size);
    if (BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0, 0) != 0 ||
        BCryptHashData(hash, reinterpret_cast<PUCHAR>(const_cast<char*>(bytes.data())),
                       static_cast<ULONG>(bytes.size()), 0) != 0 ||
        BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) != 0) {
        cleanup(); return {};
    }
    cleanup();
    std::ostringstream out;
    for (auto byte : digest) out << std::hex << std::setw(2) << std::setfill('0') << int(byte);
    return out.str();
}

bool path_has_prefix(const std::filesystem::path& path, const std::filesystem::path& root) {
    auto p = path.begin();
    auto r = root.begin();
    for (; r != root.end(); ++r, ++p) {
        if (p == path.end() || _wcsicmp(p->c_str(), r->c_str()) != 0) return false;
    }
    return true;
}

bool has_reparse_component(const std::filesystem::path& candidate,
                           const std::filesystem::path& root, std::string& error) {
    std::error_code ec;
    auto relative = std::filesystem::relative(candidate, root, ec);
    if (ec) { error = "无法验证路径组件"; return true; }
    auto current = root;
    for (const auto& component : relative) {
        current /= component;
        const auto attributes = GetFileAttributesW(current.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES) break;
        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
            error = "不允许符号链接或目录联接";
            return true;
        }
    }
    return false;
}

bool contains_reparse_in_absolute_path(const std::filesystem::path& path, std::string& error) {
    auto current = path.root_path();
    for (const auto& component : path.relative_path()) {
        current /= component;
        const auto attributes = GetFileAttributesW(current.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES) { error = "工作区路径组件不存在"; return true; }
        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
            error = "工作区不能位于符号链接或目录联接中";
            return true;
        }
    }
    return false;
}

}  // namespace

bool Workspace::set_root(const std::filesystem::path& root, std::string& error) {
    if (!root.is_absolute()) { error = "工作区必须是绝对路径"; return false; }
    if (root.native().rfind(L"\\\\", 0) == 0) {
        error = "V1.3 不支持网络共享工作区";
        return false;
    }
    std::error_code ec;
    auto input_absolute = std::filesystem::absolute(root, ec).lexically_normal();
    if (ec || contains_reparse_in_absolute_path(input_absolute, error)) return false;
    auto absolute = std::filesystem::weakly_canonical(root, ec);
    if (ec || !std::filesystem::is_directory(absolute, ec)) {
        error = "工作区目录不存在或无法访问";
        return false;
    }
    root_ = absolute;
    if (is_safe_existing_path(root_, error)) return true;
    root_.clear();
    return false;
}

std::optional<std::filesystem::path> Workspace::resolve_existing(
    const std::filesystem::path& relative, std::string& error) const {
    return resolve(relative, false, error);
}

std::optional<std::filesystem::path> Workspace::resolve(const std::filesystem::path& relative,
                                                         bool for_write, std::string& error) const {
    if (!ready()) { error = "尚未选择工作区"; return std::nullopt; }
    if (relative.is_absolute() || relative.has_root_name()) { error = "只允许工作区相对路径"; return std::nullopt; }
    std::error_code ec;
    auto candidate = (root_ / relative).lexically_normal();
    if (!path_has_prefix(candidate, root_)) { error = "路径越过工作区边界"; return std::nullopt; }
    auto parent = for_write ? candidate.parent_path() : candidate;
    if (has_reparse_component(parent, root_, error)) return std::nullopt;
    auto canonical_parent = std::filesystem::weakly_canonical(parent, ec);
    if (ec || !path_has_prefix(canonical_parent, root_)) { error = "路径越过工作区边界"; return std::nullopt; }
    candidate = for_write ? canonical_parent / candidate.filename() : canonical_parent;
    if (!for_write && !is_safe_existing_path(candidate, error)) return std::nullopt;
    if (for_write && !is_safe_existing_path(canonical_parent, error)) return std::nullopt;
    return candidate;
}

bool Workspace::is_safe_existing_path(const std::filesystem::path& path, std::string& error) const {
    const auto attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) { error = "无法读取路径属性"; return false; }
    if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) { error = "不允许符号链接或目录联接"; return false; }
    HANDLE handle = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        (attributes & FILE_ATTRIBUTE_DIRECTORY) ? FILE_FLAG_BACKUP_SEMANTICS : FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) { error = "无法验证路径边界"; return false; }
    std::vector<wchar_t> buffer(32768);
    DWORD length = GetFinalPathNameByHandleW(handle, buffer.data(), static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED);
    CloseHandle(handle);
    if (length == 0 || static_cast<size_t>(length) >= buffer.size()) { error = "无法解析最终路径"; return false; }
    std::filesystem::path final_path(buffer.data());
    std::wstring final_text = final_path.native();
    if (final_text.rfind(L"\\\\?\\", 0) == 0) final_path = final_text.substr(4);
    if (!path_has_prefix(final_path.lexically_normal(), root_)) { error = "最终路径越过工作区边界"; return false; }
    return true;
}

std::string Workspace::list_files(const std::filesystem::path& relative, int max_items, std::string& error) const {
    auto directory = resolve(relative, false, error);
    if (!directory) return {};
    std::error_code ec;
    if (!std::filesystem::is_directory(*directory, ec)) { error = "目标不是目录"; return {}; }
    max_items = std::clamp(max_items <= 0 ? kDefaultListLimit : max_items, 1, kAbsoluteListLimit);
    std::ostringstream out;
    out << "{\"items\":[";
    int count = 0;
    bool truncated = false;
    for (const auto& entry : std::filesystem::directory_iterator(*directory, ec)) {
        if (ec) break;
        if (count >= max_items) { truncated = true; break; }
        const auto attributes = GetFileAttributesW(entry.path().c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
        if (count++) out << ',';
        auto rel = std::filesystem::relative(entry.path(), root_, ec);
        out << "{\"path\":\"" << json_escape(utf8(rel)) << "\",\"directory\":"
            << (entry.is_directory(ec) ? "true" : "false") << "}";
    }
    out << "],\"truncated\":" << (truncated ? "true" : "false") << "}";
    if (ec) { error = "枚举目录失败"; return {}; }
    return out.str();
}

std::string Workspace::read_text(const std::filesystem::path& relative, std::string& error) const {
    auto path = resolve(relative, false, error);
    if (!path) return {};
    std::error_code ec;
    const auto size = std::filesystem::file_size(*path, ec);
    if (ec) { error = "文件不存在或无法读取"; return {}; }
    if (size > kMaxReadBytes) { error = "文件超过 512 KiB 读取上限"; return {}; }
    std::ifstream input(*path, std::ios::binary);
    if (!input) { error = "打开文件失败"; return {}; }
    std::string content((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    if (!is_valid_utf8(content)) { error = "UNSUPPORTED_ENCODING: 文件不是有效的 UTF-8 文本"; return {}; }
    return "{\"content\":\"" + json_escape(content) + "\",\"sha256\":\"" + sha256(content) + "\"}";
}

std::string Workspace::write_text(const std::filesystem::path& relative, const std::string& content,
                                  const std::string& expected_sha256, std::string& error) const {
    if (content.size() > kMaxWriteBytes) { error = "内容超过 1 MiB 写入上限"; return {}; }
    if (!is_valid_utf8(content)) { error = "写入内容不是有效的 UTF-8 文本"; return {}; }
    auto path = resolve(relative, true, error);
    if (!path) return {};
    std::error_code ec;
    const bool existed = std::filesystem::exists(*path, ec);
    if (ec) { error = "无法检查目标文件"; return {}; }
    if (existed) {
        if (!is_safe_existing_path(*path, error)) return {};
        std::ifstream current(*path, std::ios::binary);
        if (!current) { error = "无法读取目标文件以验证哈希"; return {}; }
        std::string current_content((std::istreambuf_iterator<char>(current)), {});
        if (expected_sha256.empty()) {
            error = "修改已有文件前必须先读取并提供 expected_sha256";
            return {};
        }
        if (sha256(current_content) != expected_sha256) {
            error = "文件已被其他程序修改，拒绝覆盖";
            return {};
        }
    }
    auto temp = *path;
    temp += L".workpilot." + std::to_wstring(GetCurrentProcessId()) + L"." +
            std::to_wstring(GetCurrentThreadId()) + L"." + std::to_wstring(GetTickCount64()) + L".tmp";
    HANDLE handle = CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (handle == INVALID_HANDLE_VALUE) { error = "无法创建唯一临时文件"; return {}; }
    DWORD written{};
    const bool wrote = WriteFile(handle, content.data(), static_cast<DWORD>(content.size()), &written, nullptr) != 0 &&
                       written == static_cast<DWORD>(content.size()) && FlushFileBuffers(handle) != 0;
    CloseHandle(handle);
    if (!wrote) { std::filesystem::remove(temp, ec); error = "写入并刷新临时文件失败"; return {}; }
    const bool exists_now = std::filesystem::exists(*path, ec);
    bool changed = ec || exists_now != existed;
    if (!changed && existed) {
        std::ifstream current(*path, std::ios::binary);
        if (!current) changed = true;
        std::string current_content((std::istreambuf_iterator<char>(current)), {});
        if (!changed) changed = sha256(current_content) != expected_sha256;
    }
    if (changed) {
        std::filesystem::remove(temp, ec);
        error = "写入期间目标文件发生变化，已取消替换";
        return {};
    }
    if (!MoveFileExW(temp.c_str(), path->c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        std::filesystem::remove(temp, ec);
        error = "原子替换文件失败";
        return {};
    }
    return "{\"path\":\"" + json_escape(utf8(relative)) + "\",\"sha256\":\"" + sha256(content) + "\"}";
}

}  // namespace workpilot
