#pragma once

#include <stddef.h>

#ifdef _WIN32
#define WP_EXPORT extern "C" __declspec(dllexport)
#define WP_CALL __cdecl
#else
#define WP_EXPORT extern "C"
#define WP_CALL
#endif

struct wp_context;
struct wp_scan;

enum wp_status {
    WP_OK = 0,
    WP_INVALID_ARGUMENT = 1,
    WP_ACCESS_DENIED = 2,
    WP_NOT_FOUND = 3,
    WP_CONFLICT = 4,
    WP_IO_ERROR = 5,
    WP_LIMIT_EXCEEDED = 6,
    WP_INTERNAL_ERROR = 7
};

enum wp_permission_mode { WP_MODE_DEFAULT = 0, WP_MODE_READ_ONLY = 1, WP_MODE_FULL_ACCESS = 2 };
enum wp_risk_level { WP_RISK_LOW = 0, WP_RISK_MEDIUM = 1, WP_RISK_HIGH = 2, WP_RISK_BLOCKED = 3 };
enum wp_permission_decision { WP_ALLOW = 0, WP_CONFIRM = 1, WP_DENY = 2 };

WP_EXPORT wp_context* WP_CALL wp_create();
WP_EXPORT void WP_CALL wp_destroy(wp_context* context);
// ABI 0x00010300. Context ownership remains with the caller; destroy all scans first.
WP_EXPORT int WP_CALL wp_abi_version();
WP_EXPORT int WP_CALL wp_set_workspace(wp_context* context, const wchar_t* root_path);
WP_EXPORT int WP_CALL wp_evaluate_permission(int mode, int risk, int mutating);
WP_EXPORT char* WP_CALL wp_list_files(wp_context* context, const wchar_t* relative_path, int max_items);
WP_EXPORT char* WP_CALL wp_read_text(wp_context* context, const wchar_t* relative_path);
WP_EXPORT char* WP_CALL wp_write_text(wp_context* context, const wchar_t* relative_path,
                                      const char* utf8_content, const char* expected_sha256);
// Options/results are UTF-8 JSON. Returned strings use CoTaskMemAlloc and wp_free.
WP_EXPORT wp_scan* WP_CALL wp_scan_begin(wp_context* context, const char* utf8_options_json);
WP_EXPORT char* WP_CALL wp_scan_next(wp_scan* scan, int max_items);
WP_EXPORT void WP_CALL wp_scan_cancel(wp_scan* scan);
WP_EXPORT void WP_CALL wp_scan_destroy(wp_scan* scan);
WP_EXPORT char* WP_CALL wp_quick_fingerprint(wp_context* context, const wchar_t* relative_path);
WP_EXPORT char* WP_CALL wp_last_error(wp_context* context);
WP_EXPORT void WP_CALL wp_free(void* value);
