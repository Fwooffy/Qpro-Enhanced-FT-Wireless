#include "../dashboard_scroll.hpp"
#include <iostream>
#include <limits>
#include <stdexcept>

namespace {
int checks = 0;
void expect(bool okay, const char *name) { ++checks; if (!okay) throw std::runtime_error(name); }
bool neutral(const qpro_controller::DashboardScroll &value) { return !value.x && !value.y && !value.touch; }

void gate_checks() {
    using namespace qpro_controller;
    const QptpSide contact{QPTP_VALID | QPTP_CONTACT, .2f, -.3f, .8f, 44};
    const Output movement{.35f, -.7f, .8f, true, true};
    for (auto mode : {Mode::Trackpad, Mode::Joystick, Mode::Swipe, Mode::Mouse})
        for (auto selection : {ControllerSide::Both, ControllerSide::Left, ControllerSide::Right})
            for (int side : {0, 1}) for (bool enabled : {false, true})
                for (bool fresh : {false, true}) for (bool available : {false, true}) {
                    Settings settings; settings.mode = mode; settings.side = selection;
                    const auto value = dashboard_scroll_output(settings, enabled, fresh, available, side, contact, movement);
                    const bool active = mode == Mode::Swipe && enabled && fresh && available && settings.applies_to(side);
                    expect(active ? value.x == movement.x && value.y == movement.y && value.touch : neutral(value),
                        "only enabled fresh selected Swipe reaches the dashboard; axes retain their signs and scale");
                }
    Settings settings; settings.mode = Mode::Swipe;
    for (int side : {-1, 2})
        expect(neutral(dashboard_scroll_output(settings, true, true, true, side, contact, movement)),
            "invalid controller side cannot scroll");
    for (auto invalid : {QptpSide{}, QptpSide{QPTP_CONTACT, 0, 0, 0, 0},
                         QptpSide{QPTP_VALID, 2, 0, 0, 0},
                         QptpSide{QPTP_VALID, 0, 0, std::numeric_limits<float>::infinity(), 0}})
        expect(neutral(dashboard_scroll_output(settings, true, true, true, 0, invalid, movement)),
            "invalid source contact cannot keep dashboard movement alive");
    for (float invalid : {std::numeric_limits<float>::quiet_NaN(), std::numeric_limits<float>::infinity(),
                          -std::numeric_limits<float>::infinity(), -1.01f, 1.01f}) {
        Output value = movement; value.x = invalid;
        expect(neutral(dashboard_scroll_output(settings, true, true, true, 0, contact, value)),
            "invalid horizontal filter output is neutral");
        value = movement; value.y = invalid;
        expect(neutral(dashboard_scroll_output(settings, true, true, true, 0, contact, value)),
            "invalid vertical filter output is neutral");
    }
    Output released = movement; released.touch = false;
    expect(neutral(dashboard_scroll_output(settings, true, true, true, 0, contact, released)),
        "completed Swipe glide does not leave axis values active");
    const auto zero = dashboard_scroll_output(settings, true, true, true, 0, contact, {0, 0, 0, true, false});
    expect(!zero.x && !zero.y && zero.touch, "a stationary contact never becomes scrolling movement");
}

void lease_and_release_checks() {
    using namespace qpro_controller;
    Settings settings; settings.mode = Mode::Swipe; settings.smoothing_ms = 0;
    ContactFilter filter; PacketLease lease;
    QptpPacket source{}; source.sequence = 1; source.monotonic_ns = 5000000000;
    source.sides[0] = {QPTP_VALID | QPTP_CONTACT, 0, 0, .8f, 44};
    const uint64_t start = 1000000000;
    expect(lease.accept(source, start), "dashboard replay accepts first source frame");
    auto frame = [&](uint64_t now, const Settings &next, bool enabled = true, bool available = true) {
        QptpPacket current{}; uint64_t revision = 0, epoch = 0;
        const bool fresh = enabled && lease.current(now, current, &revision, &epoch);
        const auto value = fresh ? filter.step_packet(current.sides[0], now, next,
            current.monotonic_ns, revision, epoch, 0, available) : Output{};
        if (!fresh) filter.clear();
        return dashboard_scroll_output(next, enabled, fresh, available, 0, current.sides[0], value);
    };
    expect(neutral(frame(start, settings)) == false, "first contact anchors a neutral axis with touch");
    source.sequence = 2; source.monotonic_ns += 20000000; source.sides[0].y = .08f;
    expect(lease.accept(source, start + 20000000), "dashboard replay accepts a moving sample");
    const auto moved = frame(start + 20000000, settings);
    expect(moved.y > 0 && !moved.x && moved.touch, "a vertical Swipe reaches the dashboard without a second derivative");
    const float velocity = settings.swipe_gain * (.08f - .003f) / .02f;
    expect(std::fabs(moved.y - velocity * (-std::expm1(-20.f / 12.f))) < .000001f,
        "dashboard receives the source velocity through its bounded render-time filter");
    expect(!lease.accept(source, start + 25000000), "duplicate source cannot renew dashboard lease");
    const auto repeated = frame(start + 25000000, settings);
    const float held_velocity = moved.y + (-std::expm1(-5.f / 12.f)) * (velocity - moved.y);
    expect(!repeated.x && std::fabs(repeated.y - held_velocity) < .000001f && repeated.touch,
        "held Swipe velocity advances render smoothing without taking a second derivative");
    source.sequence = 3; source.monotonic_ns += 20000000; source.sides[0].flags = QPTP_VALID;
    expect(lease.accept(source, start + 40000000), "dashboard replay accepts thumb lift");
    const auto glide = frame(start + 40000000, settings);
    const float decayed = velocity * std::exp(-15.f / settings.glide_ms);
    const float glide_velocity = held_velocity + (-std::expm1(-15.f / 12.f)) * (decayed - held_velocity);
    expect(glide.y > 0 && std::fabs(glide.y - glide_velocity) < .000001f && glide.touch,
        "released Swipe glide follows its original decay through the render-time recurrence");
    expect(neutral(frame(start + 240000000, settings)), "expired source stops dashboard scrolling at the existing lease boundary");
    source.sequence = 4; source.monotonic_ns += 300000000; source.sides[0].flags |= QPTP_CONTACT;
    source.sides[0].y = -.9f;
    expect(lease.accept(source, start + 350000000), "dashboard accepts a resumed reader epoch");
    const auto resumed = frame(start + 350000000, settings);
    expect(!resumed.x && !resumed.y && resumed.touch, "resuming contact reanchors without old dashboard velocity");
    source.sequence = 5; source.monotonic_ns += 20000000; source.sides[0].y = -.8f;
    expect(lease.accept(source, start + 370000000), "dashboard replay resumes motion");
    expect(frame(start + 370000000, settings).y > 0, "resumed new movement is accepted");
    expect(neutral(frame(start + 371000000, settings, false)), "Stop disables dashboard scrolling immediately");
    expect(neutral(frame(start + 372000000, settings, true, false)), "controller loss disables dashboard scrolling immediately");
    for (auto mode : {Mode::Trackpad, Mode::Joystick, Mode::Mouse}) {
        auto other = settings; other.mode = mode;
        expect(neutral(frame(start + 373000000, other)), "changing away from Swipe neutralizes dashboard output");
    }
    settings.side = ControllerSide::Right;
    expect(neutral(frame(start + 374000000, settings)), "side change releases the excluded controller");
}

void delivery_checks() {
    using namespace qpro_controller;
    DashboardScrollDelivery delivery;
    expect(!delivery.neutral(), "neutral is unconfirmed before the first component updates");
    delivery.complete({}, {});
    expect(delivery.neutral() && !delivery.error(), "successful neutral updates acknowledge all dashboard components");
    delivery.complete({.2f, -.1f, true}, {});
    expect(!delivery.neutral() && !delivery.error(), "successful motion is healthy but not neutral");
    for (int index = 0; index < 3; ++index) {
        std::array<int, 3> errors{}; errors[index] = index + 2;
        delivery.complete({}, errors);
        expect(!delivery.neutral() && delivery.error() == index + 2,
            "failure of either dashboard axis or touch blocks a neutral acknowledgement");
    }
    delivery.complete({}, {4, 7, 9});
    expect(delivery.error() == 4 && !delivery.neutral(), "first dashboard error remains available to the existing health status");
    delivery.complete({}, {});
    expect(delivery.neutral() && !delivery.error(), "successful retry confirms cleanup");
}
}

int main() {
    try {
        gate_checks(); lease_and_release_checks(); delivery_checks();
        std::cout << "Dashboard scroll checks passed: " << checks << '\n';
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "Dashboard scroll check failed: " << error.what() << '\n';
        return 1;
    }
}
