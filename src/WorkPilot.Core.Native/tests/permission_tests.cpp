#include "permission_policy.h"

#include <cassert>
#include <iostream>
#include <string>

#ifdef _WIN32
#include <Windows.h>
#include "workspace.h"
#include "scan_session.h"
#include <filesystem>
#include <fstream>
#endif

#ifdef _WIN32
void run_workspace_tests() {
    namespace fs = std::filesystem;
    const auto root = fs::temp_directory_path() / (L"workpilot-core-tests-" + std::to_wstring(GetCurrentProcessId()));
    std::error_code ec;
    fs::remove_all(root, ec);
    fs::create_directories(root);
    workpilot::Workspace workspace;
    std::string error;
    assert(workspace.set_root(root, error));
    assert(!workspace.write_text(L"note.txt", "one", "", error).empty());
    auto read = workspace.read_text(L"note.txt", error);
    assert(read.find("one") != std::string::npos);
    const auto marker = std::string("\"sha256\":\"");
    const auto start = read.find(marker) + marker.size();
    const auto hash = read.substr(start, 64);
    assert(workspace.write_text(L"note.txt", "two", "", error).empty());
    assert(error.find("expected_sha256") != std::string::npos);
    assert(!workspace.write_text(L"note.txt", "two", hash, error).empty());
    assert(workspace.read_text(L"..\\outside.txt", error).empty());
    assert(workspace.write_text(L"large.txt", std::string(workpilot::kMaxWriteBytes + 1, 'x'), "", error).empty());
    workpilot::ScanOptions options;
    workpilot::ScanSession scan(workspace, options);
    const auto page = scan.next(1, error);
    assert(error.empty());
    assert(page.find("\"items\"") != std::string::npos);
    const auto fingerprint = workpilot::quick_fingerprint(workspace, L"note.txt", error);
    assert(fingerprint.find("\"stable\":true") != std::string::npos);
    scan.cancel();
    assert(scan.next(200, error).find("\"cancelled\":true") != std::string::npos);
    fs::create_directories(root / L"target");
    std::ofstream(root / L"target" / L"linked.txt") << "linked";
    if (CreateSymbolicLinkW((root / L"link").c_str(), (root / L"target").c_str(),
                            SYMBOLIC_LINK_FLAG_DIRECTORY | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE)) {
        assert(workspace.read_text(L"link\\linked.txt", error).empty());
    }
    fs::remove_all(root, ec);
}
#endif

int main() {
    using workpilot::evaluate_permission;
    assert(evaluate_permission(WP_MODE_DEFAULT, WP_RISK_LOW, false) == WP_ALLOW);
    assert(evaluate_permission(WP_MODE_DEFAULT, WP_RISK_LOW, true) == WP_CONFIRM);
    assert(evaluate_permission(WP_MODE_FULL_ACCESS, WP_RISK_MEDIUM, true) == WP_ALLOW);
    assert(evaluate_permission(WP_MODE_READ_ONLY, WP_RISK_LOW, true) == WP_DENY);
    assert(evaluate_permission(WP_MODE_FULL_ACCESS, WP_RISK_HIGH, true) == WP_CONFIRM);
    assert(evaluate_permission(WP_MODE_FULL_ACCESS, WP_RISK_BLOCKED, false) == WP_DENY);
    assert(evaluate_permission(99, WP_RISK_LOW, false) == WP_DENY);
    static_assert(workpilot::kMaxReadBytes == 524288);
    static_assert(workpilot::kMaxWriteBytes == 1048576);
#ifdef _WIN32
    run_workspace_tests();
#endif
    std::cout << "permission_tests passed\n";
}
