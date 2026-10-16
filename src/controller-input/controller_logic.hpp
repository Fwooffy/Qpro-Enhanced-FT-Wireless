#pragma once
#include "protocol.h"
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace qpro_controller {
enum class Mode { Trackpad, Joystick, Swipe, Mouse };
enum class ControllerSide { Both, Left, Right };
struct Settings {
    Mode mode = Mode::Trackpad;
    ControllerSide side = ControllerSide::Both;
    float smoothing_ms = 18, click_on = .64f, click_off = .46f;
    float joystick_span = .6f, swipe_gain = .22f, glide_ms = 180, mouse_scale = 900;
    bool ignore_resting_thumb = false;
    float resting_size = 96, intentional_force = .28f;
    bool applies_to(int index) const {
        return (index == 0 || index == 1) && (side == ControllerSide::Both
            || (side == ControllerSide::Left && index == 0) || (side == ControllerSide::Right && index == 1));
    }
};
struct Output { float x = 0, y = 0, force = 0; bool touch = false, click = false; };

// Arrival time is local: headset and Windows monotonic clocks have different epochs.
class PacketLease {
    QptpPacket packet_{};
    uint64_t arrival_ = 0;
    uint64_t revision_ = 0, epoch_ = 0;
    bool have_ = false;
public:
    static constexpr uint64_t stale_ns = 200000000;
    bool accept(const QptpPacket &value, uint64_t now) {
        bool active = false;
        if (have_) {
            // A restarted reader or headset can have a new counter/clock epoch.
            // Accept that only once the old lease has expired. The transport is
            // trusted loopback; this is ordering protection, not authentication.
            if (value.sequence == packet_.sequence && value.monotonic_ns == packet_.monotonic_ns) return false;
            active = now < arrival_ || now - arrival_ < stale_ns;
            if (active && (value.monotonic_ns <= packet_.monotonic_ns || value.sequence <= packet_.sequence)) return false;
        }
        if (!active) ++epoch_;
        ++revision_;
        packet_ = value; arrival_ = now; have_ = true; return true;
    }
    bool current(uint64_t now, QptpPacket &out, uint64_t *revision = nullptr, uint64_t *epoch = nullptr) const {
        if (!have_ || now < arrival_ || now - arrival_ >= stale_ns) return false;
        out = packet_;
        if (revision) *revision = revision_;
        if (epoch) *epoch = epoch_;
        return true;
    }
    void clear() { packet_ = {}; arrival_ = revision_ = epoch_ = 0; have_ = false; }
};

class ContactFilter {
    bool touching_ = false, clicked_ = false;
    float x_ = 0, y_ = 0, anchor_x_ = 0, anchor_y_ = 0, vx_ = 0, vy_ = 0, force_ = 0;
    float swipe_x_ = 0, swipe_y_ = 0;
    float swipe_output_x_ = 0, swipe_output_y_ = 0;
    float mouse_previous_x_ = 0, mouse_previous_y_ = 0, mouse_output_x_ = 0, mouse_output_y_ = 0;
    uint64_t sample_tick_ = 0, frame_tick_ = 0, revision_ = 0, epoch_ = 0;
    Mode mode_ = Mode::Trackpad;
    ControllerSide side_setting_ = ControllerSide::Both;
public:
    void clear() { *this = ContactFilter{}; }
    Output step(const QptpSide &sample, uint64_t now, const Settings &settings,
                bool new_sample = true, uint64_t sample_clock = 0) {
        if (mode_ != settings.mode) { clear(); mode_ = settings.mode; new_sample = true; }
        if (!(sample.flags & QPTP_VALID) || !qptp_side_valid(&sample)
            || !std::isfinite(settings.smoothing_ms) || settings.smoothing_ms < 0
            || !std::isfinite(settings.glide_ms) || settings.glide_ms < 0
            || !std::isfinite(settings.swipe_gain) || settings.swipe_gain < 0
            || !std::isfinite(settings.joystick_span) || settings.joystick_span <= 0) {
            clear(); return {};
        }
        float frame_seconds = frame_tick_ ? (now > frame_tick_ ? float(now - frame_tick_) / 1e9f : 0) : 1.f / 60;
        frame_tick_ = now;
        if (new_sample) {
            // Source time is used only for intervals; its epoch need not match
            // the local clock which expires the lease and advances glide.
            if (!sample_clock) sample_clock = now;
            if (sample_tick_ && (sample_clock <= sample_tick_
                || sample_clock - sample_tick_ >= PacketLease::stale_ns)) {
                // A discontinuity must also release mouse contact for a frame;
                // its next real sample can then anchor without a cursor jump.
                clear(); mode_ = settings.mode; sample_tick_ = sample_clock; frame_tick_ = now;
                return {};
            }
            float seconds = sample_tick_ && sample_clock > sample_tick_
                ? std::max(float(sample_clock - sample_tick_) / 1e9f, .001f) : 1.f / 60;
            sample_tick_ = sample_clock;
            bool touch = (sample.flags & QPTP_CONTACT) != 0;
            if (settings.ignore_resting_thumb && sample.size > settings.resting_size
                && sample.force < settings.intentional_force) touch = false;
            if (touch) {
                if (!touching_) {
                    x_ = anchor_x_ = swipe_x_ = sample.x;
                    y_ = anchor_y_ = swipe_y_ = sample.y; vx_ = vy_ = 0;
                    swipe_output_x_ = swipe_output_y_ = 0;
                    mouse_previous_x_ = mouse_output_x_ = x_;
                    mouse_previous_y_ = mouse_output_y_ = y_;
                } else {
                    float target_x = sample.x, target_y = sample.y;
                    if (settings.mode == Mode::Swipe) {
                        // A small position allowance absorbs stationary noise.
                        // Its edge follows a continuing drag, so many small
                        // movements accumulate rather than being discarded.
                        constexpr float allowance = .003f;
                        float dx = sample.x - swipe_x_, dy = sample.y - swipe_y_;
                        float distance = std::hypot(dx, dy);
                        if (distance > allowance) {
                            float excess = (distance - allowance) / distance;
                            swipe_x_ += dx * excess; swipe_y_ += dy * excess;
                        }
                        target_x = swipe_x_; target_y = swipe_y_;
                    }
                    float blend = settings.smoothing_ms <= 0 ? 1.f : -std::expm1(-seconds * 1000 / settings.smoothing_ms);
                    float nx = x_ + blend * (target_x - x_), ny = y_ + blend * (target_y - y_);
                    vx_ = (nx - x_) / seconds; vy_ = (ny - y_) / seconds; x_ = nx; y_ = ny;
                }
                clicked_ = sample.force >= (clicked_ ? settings.click_off : settings.click_on);
            } else {
                clicked_ = false;
            }
            touching_ = touch; force_ = touch ? sample.force : 0;
        }
        if (!touching_) {
            float fade = settings.glide_ms > 0 ? std::exp(-frame_seconds * 1000 / settings.glide_ms) : 0;
            vx_ *= fade; vy_ *= fade;
        }
        Output result;
        result.touch = touching_; result.click = clicked_; result.force = force_;
        switch (settings.mode) {
        case Mode::Trackpad:
            result.x = touching_ ? x_ : 0; result.y = touching_ ? y_ : 0; break;
        case Mode::Mouse:
            if (touching_ && frame_seconds > 0) {
                // Spread source-rate position steps across render frames.
                // Keep the two-frame average and ease its target in local
                // time; touch and pressure clicks bypass this position filter.
                constexpr float mouse_render_ms = 12.f;
                float mouse_blend = -std::expm1(-frame_seconds * 1000 / mouse_render_ms);
                float mouse_target_x = (x_ + mouse_previous_x_) * .5f;
                float mouse_target_y = (y_ + mouse_previous_y_) * .5f;
                mouse_output_x_ += mouse_blend * (mouse_target_x - mouse_output_x_);
                mouse_output_y_ += mouse_blend * (mouse_target_y - mouse_output_y_);
                mouse_previous_x_ = x_; mouse_previous_y_ = y_;
            }
            result.x = touching_ ? mouse_output_x_ : 0;
            result.y = touching_ ? mouse_output_y_ : 0;
            break;
        case Mode::Joystick:
            result.x = touching_ ? std::clamp((x_ - anchor_x_) / settings.joystick_span, -1.f, 1.f) : 0;
            result.y = touching_ ? std::clamp((y_ - anchor_y_) / settings.joystick_span, -1.f, 1.f) : 0;
            {
                constexpr float deadband = .02f;
                float distance = std::hypot(result.x, result.y);
                if (distance <= deadband) result.x = result.y = 0;
                else {
                    // Preserve direction and the original square full range.
                    // The gain approaches one continuously at each edge.
                    float edge = std::max(std::fabs(result.x), std::fabs(result.y));
                    float gain = (distance - deadband) / (distance - deadband * edge);
                    result.x *= gain; result.y *= gain;
                }
            }
            break;
        case Mode::Swipe:
            result.x = std::clamp(vx_ * settings.swipe_gain, -1.f, 1.f);
            result.y = std::clamp(vy_ * settings.swipe_gain, -1.f, 1.f);
            // Keep slow deliberate drags. The cutoff ends released inertia;
            // applying it during contact used to erase gentle swipes entirely.
            if (!touching_ && std::fabs(result.x) < .015f) result.x = 0;
            if (!touching_ && std::fabs(result.y) < .015f) result.y = 0;
            if (!touching_ && result.x == 0) swipe_output_x_ = 0;
            if (!touching_ && result.y == 0) swipe_output_y_ = 0;
            result.touch = touching_ || result.x != 0 || result.y != 0;
            if (!result.touch) {
                swipe_output_x_ = swipe_output_y_ = 0;
            } else if (frame_seconds > 0) {
                // Ease velocity steps in render time while keeping the original
                // contact/inertia lifetime. Invalid input still clears all state;
                // neither an ended gesture nor a new contact inherits a tail.
                constexpr float swipe_render_ms = 12.f;
                float swipe_blend = -std::expm1(-frame_seconds * 1000 / swipe_render_ms);
                swipe_output_x_ += swipe_blend * (result.x - swipe_output_x_);
                swipe_output_y_ += swipe_blend * (result.y - swipe_output_y_);
            }
            result.x = swipe_output_x_; result.y = swipe_output_y_;
            break;
        }
        return result;
    }
    Output step_packet(const QptpSide &sample, uint64_t now, const Settings &settings,
                       uint64_t source_clock, uint64_t revision, uint64_t epoch, int side = -1, bool available = true) {
        // A deactivated or excluded controller cannot revive a retained click
        // packet. Clear its key too so a later adoption starts at a new anchor.
        if (!available || (side >= 0 && !settings.applies_to(side))) { clear(); return {}; }
        // A reconnect or an expired lease must not inherit an old anchor or
        // velocity, even if no RunFrame happened during the expired interval.
        if (epoch_ != epoch || side_setting_ != settings.side) clear();
        auto result = step(sample, now, settings, revision_ != revision, source_clock);
        revision_ = revision; epoch_ = epoch; side_setting_ = settings.side;
        return result;
    }
};
}
