#pragma once

#include "workpilot_core.h"

namespace workpilot {

constexpr size_t kMaxReadBytes = 512 * 1024;
constexpr size_t kMaxWriteBytes = 1024 * 1024;
constexpr int kDefaultListLimit = 200;
constexpr int kAbsoluteListLimit = 2000;

int evaluate_permission(int mode, int risk, bool mutating) noexcept;

}  // namespace workpilot

