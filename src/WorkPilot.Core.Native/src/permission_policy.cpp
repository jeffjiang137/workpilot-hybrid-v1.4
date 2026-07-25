#include "permission_policy.h"

namespace workpilot {

int evaluate_permission(int mode, int risk, bool mutating) noexcept {
    if (risk < WP_RISK_LOW || risk > WP_RISK_BLOCKED ||
        mode < WP_MODE_DEFAULT || mode > WP_MODE_FULL_ACCESS) {
        return WP_DENY;
    }
    if (risk == WP_RISK_BLOCKED || (mode == WP_MODE_READ_ONLY && mutating)) {
        return WP_DENY;
    }
    if (risk >= WP_RISK_HIGH) {
        return WP_CONFIRM;
    }
    if (mutating && mode != WP_MODE_FULL_ACCESS) {
        return WP_CONFIRM;
    }
    return WP_ALLOW;
}

}  // namespace workpilot

