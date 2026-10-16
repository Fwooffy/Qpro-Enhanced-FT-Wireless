#pragma once
#include "controller_logic.hpp"
#include <array>
#include <cctype>
#include <cstdlib>
#include <string>

namespace qpro_controller {
struct Configuration { bool enabled = false; Settings input; };
inline bool same_configuration(const Configuration &a, const Configuration &b) {
    const auto &x = a.input; const auto &y = b.input;
    return a.enabled == b.enabled && x.mode == y.mode && x.side == y.side
        && x.smoothing_ms == y.smoothing_ms && x.click_on == y.click_on && x.click_off == y.click_off
        && x.joystick_span == y.joystick_span && x.swipe_gain == y.swipe_gain && x.glide_ms == y.glide_ms
        && x.mouse_scale == y.mouse_scale && x.ignore_resting_thumb == y.ignore_resting_thumb
        && x.resting_size == y.resting_size && x.intentional_force == y.intentional_force;
}
// The caller holds its state mutex. Keep only the newest complete settings;
// rendering never reads a file or drains a queue of obsolete configurations.
class LatestConfiguration {
    Configuration value_;
    bool valid_ = false;
    uint64_t revision_ = 1;
public:
    bool publish(Configuration value, bool valid) {
        if (!valid) value = {};
        if (valid == valid_ && same_configuration(value, value_)) return false;
        value_ = value; valid_ = valid; ++revision_; return true;
    }
    bool take(uint64_t &seen, Configuration &value, bool &valid) const {
        if (seen == revision_) return false;
        value = value_; valid = valid_; seen = revision_; return true;
    }
    uint64_t revision() const { return revision_; }
};

template<size_t Count> class ControllerDiscovery {
    std::array<bool, Count> known_{};
    bool enabled_ = false;
    uint64_t due_ = 0, scan_due_ = 0;
    size_t cursor_ = 0;
    static constexpr size_t batch = Count < 8 ? Count : 8;
    static constexpr uint64_t interval = 1000000000 / ((Count + batch - 1) / batch);
public:
    // Activate may precede final role/profile properties. Keep the index in
    // bounded discovery until adoption succeeds, even when it was used before.
    void activated(size_t id) { if (id < Count) known_[id] = false; }
    void remember(size_t id) { if (id < Count) known_[id] = true; }
    std::array<bool, Count> poll(bool enabled, uint64_t now) {
        std::array<bool, Count> selected{};
        if (!enabled) { enabled_ = false; return selected; }
        bool first = !enabled_; enabled_ = true;
        if (first) {
            selected.fill(true); cursor_ = 0; due_ = now + 1000000000; scan_due_ = now + interval;
            return selected;
        }
        if (now >= due_) { selected = known_; due_ = now + 1000000000; }
        if (now >= scan_due_) {
            // An inactive pre-existing driver can activate without our wrapper
            // and receive a new index. Cover every unknown slot once a second,
            // spreading these reads across frames rather than a 64-slot burst.
            for (size_t count = 0; count < batch; ++count) {
                size_t id = cursor_; cursor_ = (cursor_ + 1) % Count;
                if (!known_[id]) selected[id] = true;
            }
            scan_due_ += interval;
            if (now >= scan_due_ && now - scan_due_ >= 1000000000) scan_due_ = now + interval;
        }
        return selected;
    }
};
template<size_t Count> class ControllerAvailability {
    std::array<uint64_t, Count> inactive_{};
public:
    void activated(size_t id) { if (id < Count) inactive_[id] = 0; }
    void deactivated(size_t id, uint64_t container) { if (id < Count) inactive_[id] = container; }
    bool can_adopt(size_t id, uint64_t container) {
        if (id >= Count || !container || inactive_[id] == container) return false;
        inactive_[id] = 0; return true;
    }
};

// Deliberately accept only a flat, bounded object with this add-on's known keys.
// An invalid file disables input; settings from a partly parsed file never escape.
inline bool parse_settings(const std::string &text, Configuration &result) {
    if (text.size() > 8192) return false;
    size_t cursor = 0;
    auto space = [&] { while (cursor < text.size() && std::isspace((unsigned char)text[cursor])) ++cursor; };
    auto take = [&](char c) { space(); if (cursor == text.size() || text[cursor] != c) return false; ++cursor; return true; };
    auto quoted = [&](std::string &value) {
        if (!take('"')) return false; size_t begin = cursor;
        while (cursor < text.size() && text[cursor] != '"') {
            if (text[cursor] == '\\' || (unsigned char)text[cursor] < 32) return false; ++cursor;
        }
        if (cursor == text.size()) return false;
        value = text.substr(begin, cursor - begin); ++cursor; return true;
    };
    Configuration parsed;
    std::string seen = "|";
    if (!take('{')) return false;
    space();
    if (cursor < text.size() && text[cursor] != '}') for (;;) {
        std::string key; if (!quoted(key) || !take(':') || seen.find("|" + key + "|") != std::string::npos) return false;
        seen += key + "|"; space();
        if (key == "side") {
            std::string side; if (!quoted(side)) return false;
            if (side == "both") parsed.input.side = ControllerSide::Both;
            else if (side == "left") parsed.input.side = ControllerSide::Left;
            else if (side == "right") parsed.input.side = ControllerSide::Right;
            else return false;
        } else if (key == "mode") {
            std::string mode; if (!quoted(mode)) return false;
            if (mode == "trackpad") parsed.input.mode = Mode::Trackpad;
            else if (mode == "joystick") parsed.input.mode = Mode::Joystick;
            else if (mode == "swipe") parsed.input.mode = Mode::Swipe;
            else if (mode == "mouse") parsed.input.mode = Mode::Mouse;
            else return false;
        } else if (key == "enabled" || key == "ignoreRestingThumb") {
            bool value;
            if (text.compare(cursor, 4, "true") == 0) { value = true; cursor += 4; }
            else if (text.compare(cursor, 5, "false") == 0) { value = false; cursor += 5; }
            else return false;
            if (key == "enabled") parsed.enabled = value; else parsed.input.ignore_resting_thumb = value;
        } else {
            if (cursor == text.size() || (text[cursor] != '-' && !std::isdigit((unsigned char)text[cursor]))) return false;
            char *end; double value = std::strtod(text.c_str() + cursor, &end);
            if (end == text.c_str() + cursor || !std::isfinite(value)) return false;
            cursor = (size_t)(end - text.c_str()); float *target = nullptr; float low = 0, high = 1;
            if (key == "smoothingMs") { target = &parsed.input.smoothing_ms; high = 250; }
            else if (key == "clickOn") target = &parsed.input.click_on;
            else if (key == "clickOff") target = &parsed.input.click_off;
            else if (key == "joystickSpan") { target = &parsed.input.joystick_span; low = .1f; high = 2; }
            else if (key == "swipeGain") { target = &parsed.input.swipe_gain; low = .01f; high = 5; }
            else if (key == "glideMs") { target = &parsed.input.glide_ms; high = 2000; }
            else if (key == "mouseScale") { target = &parsed.input.mouse_scale; high = 4000; }
            else if (key == "restingSize") { target = &parsed.input.resting_size; high = 512; }
            else if (key == "intentionalForce") target = &parsed.input.intentional_force;
            else return false;
            if (value < low || value > high) return false; *target = (float)value;
        }
        space(); if (cursor < text.size() && text[cursor] == '}') break;
        if (!take(',')) return false;
    }
    if (!take('}')) return false; space();
    if (cursor != text.size() || parsed.input.click_off >= parsed.input.click_on) return false;
    result = parsed; return true;
}
struct MouseUpdate { int dx = 0, dy = 0; bool down = false, up = false; };
class MouseMotion {
    bool active_ = false, pressed_ = false;
    float x_ = 0, y_ = 0;
    double carry_x_ = 0, carry_y_ = 0;
public:
    bool pressed() const { return pressed_; }
    MouseUpdate step(const Output &value, bool allowed, float scale) {
        MouseUpdate update;
        if (!allowed || !value.touch || !std::isfinite(value.x) || !std::isfinite(value.y)
            || std::fabs(value.x) > 1 || std::fabs(value.y) > 1
            || !std::isfinite(scale) || scale < 0 || scale > 4000) {
            update.up = pressed_; pressed_ = active_ = false; carry_x_ = carry_y_ = 0; return update;
        }
        if (active_) {
            carry_x_ += (double(value.x) - x_) * scale; carry_y_ -= (double(value.y) - y_) * scale;
            update.dx = (int)carry_x_; update.dy = (int)carry_y_;
            carry_x_ -= update.dx; carry_y_ -= update.dy;
        }
        x_ = value.x; y_ = value.y; active_ = true;
        update.down = value.click && !pressed_; update.up = !value.click && pressed_; pressed_ = value.click;
        return update;
    }
};
class StereoMouseMotion {
    std::array<MouseMotion, 2> motion_;
    std::array<bool, 2> clicks_{};
    bool pressed_ = false;
    void button(MouseUpdate &update) {
        bool pressed = clicks_[0] || clicks_[1];
        update.down = pressed && !pressed_; update.up = !pressed && pressed_;
        pressed_ = pressed;
    }
public:
    bool pressed() const { return pressed_; }
    MouseUpdate step(const std::array<Output, 2> &values, bool allowed, const Settings &settings) {
        MouseUpdate update;
        for (int side = 0; side < 2; ++side) {
            bool selected = allowed && settings.applies_to(side);
            auto change = motion_[side].step(values[side], selected, settings.mouse_scale);
            update.dx += change.dx; update.dy += change.dy;
            clicks_[side] = motion_[side].pressed();
        }
        // One global button edge follows the selected clicks together; one
        // thumb releasing must not cancel the other thumb's held click.
        button(update); return update;
    }
    MouseUpdate release(int side) {
        MouseUpdate update;
        if (side >= 0 && side < 2) { motion_[side].step({}, false, 0); clicks_[side] = false; }
        button(update); return update;
    }
};
}
