#include "scan_session.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <regex>
#include <sstream>

namespace workpilot {
namespace {

constexpr int kPageLimit = 200;
constexpr size_t kMaxJsonBytes = 2 * 1024 * 1024;
constexpr size_t kEdgeBytes = 65536;
const std::array<std::wstring, 12> kHardIgnored = {
    L".git", L".svn", L".hg", L".vs", L"node_modules", L"bin", L"obj",
    L"artifacts", L"dist", L"build", L"packages", L".cache" };

std::string utf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) return {};
    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        result.data(), size, nullptr, nullptr);
    return result;
}

std::string escape_json(const std::string& value) {
    std::ostringstream out;
    for (const unsigned char c : value) {
        if (c == '\\') out << "\\\\"; else if (c == '"') out << "\\\"";
        else if (c == '\n') out << "\\n"; else if (c == '\r') out << "\\r";
        else if (c == '\t') out << "\\t"; else if (c < 0x20) out << "?"; else out << c;
    }
    return out.str();
}

std::wstring lower_invariant(const std::wstring& value) {
    if (value.empty()) return {};
    const int size = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr, 0);
    if (size <= 0) return value;
    std::wstring result(static_cast<size_t>(size), L'\0');
    LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, value.data(), static_cast<int>(value.size()),
        result.data(), size, nullptr, nullptr, 0);
    return result;
}

std::wstring glob_regex(const std::wstring& pattern) {
    std::wstring out = L"^";
    for (size_t i = 0; i < pattern.size(); ++i) {
        const wchar_t c = pattern[i];
        if (c == L'*' && i + 1 < pattern.size() && pattern[i + 1] == L'*') { out += L".*"; ++i; }
        else if (c == L'*') out += L"[^/]*";
        else if (c == L'?') out += L"[^/]";
        else { if (wcschr(L".^$|()[]{}+\\", c)) out += L'\\'; out += c; }
    }
    return out + L"$";
}

bool glob_matches(std::wstring pattern, const std::wstring& path, bool directory) {
    if (!pattern.empty() && pattern.back() == L'/') {
        if (!directory) return false;
        pattern.pop_back();
    }
    const bool rooted = !pattern.empty() && pattern.front() == L'/';
    if (rooted) pattern.erase(pattern.begin()); else pattern = L"**/" + pattern;
    auto expression = glob_regex(lower_invariant(pattern));
    if (!rooted && expression.rfind(L"^.*/", 0) == 0) expression.replace(0, 4, L"^(?:.*/)?");
    try { return std::regex_match(lower_invariant(path), std::wregex(expression)); }
    catch (const std::regex_error&) { return false; }
}

std::string sha256(const std::vector<unsigned char>& bytes) {
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{}; DWORD object_size{}; DWORD copied{};
    std::vector<unsigned char> object; std::array<unsigned char, 32> digest{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) return {};
    const auto close = [&] { if (hash) BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); };
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size),
        sizeof(object_size), &copied, 0) != 0) { close(); return {}; }
    object.resize(object_size);
    if (BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0, 0) != 0 ||
        BCryptHashData(hash, const_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()), 0) != 0 ||
        BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) != 0) { close(); return {}; }
    close(); std::ostringstream out;
    for (const auto byte : digest) out << std::hex << std::setw(2) << std::setfill('0') << static_cast<int>(byte);
    return out.str();
}

long long unix_milliseconds(const std::filesystem::file_time_type& time) {
    const auto system = std::chrono::time_point_cast<std::chrono::milliseconds>(
        time - std::filesystem::file_time_type::clock::now() + std::chrono::system_clock::now());
    return system.time_since_epoch().count();
}

}  // namespace

bool parse_scan_options(const std::string& json, ScanOptions& options, std::string& error) {
    if (json.size() > 110000 || json.find("\"version\":1") == std::string::npos) {
        error = "扫描选项缺少 version=1 或内容过大"; return false;
    }
    const std::regex allowed(R"(^\s*\{\s*"version"\s*:\s*1\s*,\s*"include_hidden"\s*:\s*(true|false)\s*,\s*"max_depth"\s*:\s*([0-9]+)\s*,\s*"max_files"\s*:\s*([0-9]+)\s*,\s*"ignore_rules"\s*:\s*\[(.*)\]\s*\}\s*$)");
    std::smatch match;
    if (!std::regex_match(json, match, allowed)) { error = "扫描选项 JSON 结构无效或包含未知字段"; return false; }
    options.include_hidden = match[1] == "true";
    options.max_depth = std::stoi(match[2]); options.max_files = std::stoi(match[3]);
    if (options.max_depth < 0 || options.max_depth > 32 || options.max_files < 1 || options.max_files > 100000) {
        error = "扫描深度或文件数量超过安全上限"; return false;
    }
    const std::string rules = match[4];
    const std::regex item(R"("((?:\\.|[^"\\])*)")");
    for (auto it = std::sregex_iterator(rules.begin(), rules.end(), item); it != std::sregex_iterator(); ++it) {
        std::string text = (*it)[1];
        if (text.size() > 500 || options.ignore_rules.size() >= 200) { error = "忽略规则超过安全上限"; return false; }
        const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
        if (size <= 0) { error = "忽略规则必须是 UTF-8"; return false; }
        std::wstring wide(static_cast<size_t>(size), L'\0');
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), wide.data(), size);
        options.ignore_rules.push_back(std::move(wide));
    }
    return true;
}

ScanSession::ScanSession(const Workspace& workspace, ScanOptions options)
    : workspace_(workspace), options_(std::move(options)) {
    directories_.push_back({workspace_.root(), {}, 0});
}

bool ScanSession::ignored(const std::filesystem::path& relative, bool directory) const {
    for (const auto& part : relative) for (const auto& hard : kHardIgnored)
        if (_wcsicmp(part.c_str(), hard.c_str()) == 0) return true;
    bool value = false;
    const auto path = relative.generic_wstring();
    for (auto rule : options_.ignore_rules) {
        if (rule.empty() || rule.front() == L'#') continue;
        const bool negated = rule.front() == L'!'; if (negated) rule.erase(rule.begin());
        if (glob_matches(rule, path, directory)) value = !negated;
    }
    return value;
}

bool ScanSession::load_directory(std::string& error) {
    entries_.clear(); entry_index_ = 0;
    while (!directories_.empty()) {
        if (cancelled_.load()) return false;
        const auto work = directories_.front(); directories_.pop_front(); ++directories_seen_;
        std::error_code ec;
        for (const auto& item : std::filesystem::directory_iterator(work.absolute, ec)) {
            if (ec) break;
            const DWORD attributes = GetFileAttributesW(item.path().c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_SYSTEM)) != 0) continue;
            if (!options_.include_hidden && (attributes & FILE_ATTRIBUTE_HIDDEN) != 0) continue;
            const bool directory = (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            const auto relative = work.relative / item.path().filename();
            if (ignored(relative, directory)) continue;
            if (directory) {
                if (work.depth < options_.max_depth) directories_.push_back({item.path(), relative, work.depth + 1});
            } else entries_.push_back({item.path(), relative, attributes, false});
        }
        if (ec) { error = "枚举工作区目录失败"; return false; }
        std::sort(entries_.begin(), entries_.end(), [](const Entry& left, const Entry& right) {
            return _wcsicmp(left.relative.c_str(), right.relative.c_str()) < 0;
        });
        if (!entries_.empty()) return true;
    }
    done_ = true; return false;
}

std::string ScanSession::next(int max_items, std::string& error) {
    max_items = std::clamp(max_items, 1, kPageLimit);
    std::ostringstream items; int emitted = 0;
    while (!cancelled_.load() && !done_ && emitted < max_items) {
        if (entry_index_ >= entries_.size() && !load_directory(error)) break;
        if (!error.empty() || done_) break;
        const auto& item = entries_[entry_index_++];
        std::string verify_error;
        if (!workspace_.resolve_existing(item.relative, verify_error)) continue;
        if (files_seen_ >= options_.max_files) { limit_reached_ = true; done_ = true; break; }
        std::error_code ec; const auto size = std::filesystem::file_size(item.absolute, ec);
        const auto modified = std::filesystem::last_write_time(item.absolute, ec); if (ec) continue;
        const auto relative = item.relative.generic_wstring(); const auto filename = item.relative.filename().wstring();
        if (emitted++) items << ','; ++files_seen_;
        items << "{\"relative_path\":\"" << escape_json(utf8(relative)) << "\",\"path_key\":\""
              << escape_json(utf8(lower_invariant(relative))) << "\",\"file_name\":\""
              << escape_json(utf8(filename)) << "\",\"extension\":\""
              << escape_json(utf8(item.relative.extension().wstring())) << "\",\"size_bytes\":" << size
              << ",\"modified_unix_ms\":" << unix_milliseconds(modified) << ",\"attributes\":" << item.attributes << '}';
        if (items.tellp() > static_cast<std::streampos>(kMaxJsonBytes - 1024)) break;
    }
    if (cancelled_.load()) done_ = true;
    return "{\"version\":1,\"done\":" + std::string(done_ ? "true" : "false") +
        ",\"cancelled\":" + (cancelled_.load() ? "true" : "false") +
        ",\"limit_reached\":" + (limit_reached_ ? "true" : "false") +
        ",\"directories_seen\":" + std::to_string(directories_seen_) +
        ",\"files_seen\":" + std::to_string(files_seen_) + ",\"items\":[" + items.str() + "]}";
}

std::string quick_fingerprint(const Workspace& workspace, const std::filesystem::path& relative,
                              std::string& error) {
    auto path = workspace.resolve_existing(relative, error); if (!path) return {};
    std::error_code ec; const auto before_size = std::filesystem::file_size(*path, ec);
    const auto before_time = std::filesystem::last_write_time(*path, ec); if (ec) { error = "无法读取文件元数据"; return {}; }
    std::ifstream input(*path, std::ios::binary); if (!input) { error = "无法打开文件计算指纹"; return {}; }
    std::vector<unsigned char> bytes(16); memcpy(bytes.data(), &before_size, 8);
    const auto before_ms = unix_milliseconds(before_time); memcpy(bytes.data() + 8, &before_ms, 8);
    const size_t first_size = static_cast<size_t>(std::min<uintmax_t>(before_size, kEdgeBytes));
    const size_t last_start = before_size <= kEdgeBytes ? first_size : static_cast<size_t>(std::max<uintmax_t>(kEdgeBytes, before_size - kEdgeBytes));
    std::vector<char> buffer(first_size); input.read(buffer.data(), static_cast<std::streamsize>(first_size));
    bytes.insert(bytes.end(), buffer.begin(), buffer.end());
    if (last_start < before_size) {
        const size_t last_size = static_cast<size_t>(before_size) - last_start; buffer.resize(last_size);
        input.clear(); input.seekg(static_cast<std::streamoff>(last_start)); input.read(buffer.data(), static_cast<std::streamsize>(last_size));
        bytes.insert(bytes.end(), buffer.begin(), buffer.end());
    }
    const auto after_size = std::filesystem::file_size(*path, ec); const auto after_time = std::filesystem::last_write_time(*path, ec);
    const bool stable = !ec && before_size == after_size && before_time == after_time;
    return "{\"size_bytes\":" + std::to_string(before_size) + ",\"modified_unix_ms\":" +
        std::to_string(before_ms) + ",\"quick_fingerprint\":\"" + sha256(bytes) +
        "\",\"stable\":" + (stable ? "true" : "false") + "}";
}

}  // namespace workpilot
