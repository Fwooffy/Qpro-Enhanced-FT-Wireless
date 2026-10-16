#pragma once
#include "controller_logic.hpp"
#include <array>

namespace qpro_controller {
struct DashboardScroll { float x = 0, y = 0; bool touch = false; };

// Swipe already represents velocity. Keep dashboard scrolling separate from
// the user-bindable surface so its absolute Trackpad position cannot scroll.
inline DashboardScroll dashboard_scroll_output(const Settings &settings, bool enabled,
        bool fresh, bool available, int side, const QptpSide &sample, const Output &value) {
    if (!enabled || !fresh || !available || settings.mode != Mode::Swipe || !settings.applies_to(side)
        || !(sample.flags & QPTP_VALID) || !qptp_side_valid(&sample) || !value.touch
        || !std::isfinite(value.x) || !std::isfinite(value.y)
        || std::fabs(value.x) > 1 || std::fabs(value.y) > 1) return {};
    // Released Swipe inertia is intentional while the source lease is fresh.
    return {value.x, value.y, value.touch};
}

class DashboardScrollDelivery {
    int error_ = 0;
    bool neutral_ = false;
public:
    void complete(const DashboardScroll &value, const std::array<int, 3> &errors) {
        error_ = 0;
        for (int error : errors) if (error && !error_) error_ = error;
        neutral_ = !error_ && !value.x && !value.y && !value.touch;
    }
    int error() const { return error_; }
    bool neutral() const { return neutral_; }
};
}
