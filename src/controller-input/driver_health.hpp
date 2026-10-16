#pragma once
#include "settings.hpp"
#include <cstring>
#include <string>

namespace qpro_controller {
constexpr char health_request[] = "QPRO_CTRL_HEALTH_V1 ";
inline bool decode_health_request(const uint8_t *bytes, size_t count, std::string &nonce) {
    constexpr size_t prefix = sizeof health_request - 1;
    if (count != prefix + 32 || std::memcmp(bytes, health_request, prefix)) return false;
    for (size_t index = prefix; index < count; ++index)
        if (!((bytes[index] >= '0' && bytes[index] <= '9') || (bytes[index] >= 'a' && bytes[index] <= 'f'))) return false;
    nonce.assign(reinterpret_cast<const char *>(bytes + prefix), 32); return true;
}
struct DriverHealth {
    bool enabled = false, settings_valid = false, neutral = false;
    Mode mode = Mode::Trackpad; ControllerSide side = ControllerSide::Both;
    std::array<bool, 2> adopted{}, successful{};
    std::array<int, 2> input_errors{};
    int mouse_error = 0; uint64_t restore_errors = 0, settings_revision = 0, applied_revision = 0;
};
class ComponentDelivery {
    int error_ = 0;
    bool neutral_ = false;
public:
    void complete(const Output &value, const std::array<int, 5> &errors) {
        error_ = 0;
        for (int error : errors) if (error && !error_) error_ = error;
        neutral_ = !error_ && !value.x && !value.y && !value.force && !value.touch && !value.click;
    }
    int error() const { return error_; }
    bool neutral() const { return neutral_; }
};
inline std::string encode_health(const DriverHealth &state, const std::string &nonce) {
    auto boolean = [](bool value) { return value ? "true" : "false"; };
    auto pair = [&](const std::array<bool, 2> &value) {
        return std::string("[") + boolean(value[0]) + "," + boolean(value[1]) + "]";
    };
    const char *mode = state.mode == Mode::Trackpad ? "trackpad" : state.mode == Mode::Joystick ? "joystick"
        : state.mode == Mode::Swipe ? "swipe" : "mouse";
    const char *side = state.side == ControllerSide::Both ? "both" : state.side == ControllerSide::Left ? "left" : "right";
    return std::string("{\"protocol\":\"QPRO-CTRL-HEALTH-v1\",\"nonce\":\"") + nonce
        + "\",\"enabled\":" + boolean(state.enabled) + ",\"settingsValid\":" + boolean(state.settings_valid)
        + ",\"mode\":\"" + mode + "\",\"side\":\"" + side + "\",\"adoptedSides\":" + pair(state.adopted)
        + ",\"successfulSides\":" + pair(state.successful) + ",\"neutral\":" + boolean(state.neutral)
        + ",\"inputErrors\":[" + std::to_string(state.input_errors[0]) + "," + std::to_string(state.input_errors[1])
        + "],\"mouseError\":" + std::to_string(state.mouse_error) + ",\"restoreErrors\":" + std::to_string(state.restore_errors)
        + ",\"settingsRevision\":" + std::to_string(state.settings_revision) + ",\"appliedRevision\":" + std::to_string(state.applied_revision) + "}";
}
// Desired button state and accepted OS state are separate. Failed presses and
// releases are retried; never emit an unowned release after a rejected press.
class MouseDelivery {
    bool pressed_ = false;
    int error_ = 0;
public:
    MouseUpdate plan(MouseUpdate change, bool desired) const {
        change.down = desired && !pressed_; change.up = !desired && pressed_; return change;
    }
    void complete(const MouseUpdate &change, bool success, int error) {
        if (!change.dx && !change.dy && !change.down && !change.up) { error_ = 0; return; }
        error_ = success ? 0 : error;
        if (success) { if (change.down) pressed_ = true; if (change.up) pressed_ = false; }
    }
    bool pressed() const { return pressed_; }
    int error() const { return error_; }
};
}
