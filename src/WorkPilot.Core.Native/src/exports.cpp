#include "workpilot_core.h"

#include "permission_policy.h"
#include "scan_session.h"
#include "workspace.h"

#include <Windows.h>
#include <cstring>
#include <exception>
#include <string>
#include <memory>

struct wp_context {
    workpilot::Workspace workspace;
    std::string last_error;
};

struct wp_scan {
    wp_context* owner;
    std::unique_ptr<workpilot::ScanSession> session;
};

namespace {

char* copy_result(const std::string& value) {
    auto* result = static_cast<char*>(CoTaskMemAlloc(value.size() + 1));
    if (!result) return nullptr;
    memcpy(result, value.data(), value.size());
    result[value.size()] = '\0';
    return result;
}

char* invoke(wp_context* context, const auto& operation) {
    if (!context) return nullptr;
    context->last_error.clear();
    try {
        auto result = operation(context->last_error);
        return context->last_error.empty() ? copy_result(result) : nullptr;
    } catch (const std::exception& error) {
        context->last_error = error.what();
    } catch (...) {
        context->last_error = "未知核心错误";
    }
    return nullptr;
}

}  // namespace

wp_context* WP_CALL wp_create() {
    try { return new wp_context(); } catch (...) { return nullptr; }
}

int WP_CALL wp_abi_version() { return 0x00010300; }

void WP_CALL wp_destroy(wp_context* context) { delete context; }

int WP_CALL wp_set_workspace(wp_context* context, const wchar_t* root_path) {
    if (!context || !root_path) return WP_INVALID_ARGUMENT;
    context->last_error.clear();
    return context->workspace.set_root(root_path, context->last_error) ? WP_OK : WP_ACCESS_DENIED;
}

int WP_CALL wp_evaluate_permission(int mode, int risk, int mutating) {
    return workpilot::evaluate_permission(mode, risk, mutating != 0);
}

char* WP_CALL wp_list_files(wp_context* context, const wchar_t* relative_path, int max_items) {
    return invoke(context, [&](std::string& error) {
        return context->workspace.list_files(relative_path ? relative_path : L"", max_items, error);
    });
}

char* WP_CALL wp_read_text(wp_context* context, const wchar_t* relative_path) {
    if (!relative_path) return nullptr;
    return invoke(context, [&](std::string& error) { return context->workspace.read_text(relative_path, error); });
}

char* WP_CALL wp_write_text(wp_context* context, const wchar_t* relative_path,
                            const char* utf8_content, const char* expected_sha256) {
    if (!relative_path || !utf8_content) return nullptr;
    return invoke(context, [&](std::string& error) {
        return context->workspace.write_text(relative_path, utf8_content,
            expected_sha256 ? expected_sha256 : "", error);
    });
}

wp_scan* WP_CALL wp_scan_begin(wp_context* context, const char* utf8_options_json) {
    if (!context || !utf8_options_json || !context->workspace.ready()) return nullptr;
    context->last_error.clear();
    try {
        workpilot::ScanOptions options;
        if (!workpilot::parse_scan_options(utf8_options_json, options, context->last_error)) return nullptr;
        return new wp_scan{context, std::make_unique<workpilot::ScanSession>(context->workspace, std::move(options))};
    } catch (const std::exception& error) { context->last_error = error.what(); }
    catch (...) { context->last_error = "无法创建扫描会话"; }
    return nullptr;
}

char* WP_CALL wp_scan_next(wp_scan* scan, int max_items) {
    if (!scan || !scan->owner || !scan->session) return nullptr;
    return invoke(scan->owner, [&](std::string& error) { return scan->session->next(max_items, error); });
}

void WP_CALL wp_scan_cancel(wp_scan* scan) {
    if (scan && scan->session) scan->session->cancel();
}

void WP_CALL wp_scan_destroy(wp_scan* scan) { delete scan; }

char* WP_CALL wp_quick_fingerprint(wp_context* context, const wchar_t* relative_path) {
    if (!relative_path) return nullptr;
    return invoke(context, [&](std::string& error) {
        return workpilot::quick_fingerprint(context->workspace, relative_path, error);
    });
}

char* WP_CALL wp_last_error(wp_context* context) {
    return copy_result(context ? context->last_error : "核心上下文无效");
}

void WP_CALL wp_free(void* value) { CoTaskMemFree(value); }
