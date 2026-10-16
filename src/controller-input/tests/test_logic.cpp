#include "../driver_health.hpp"
#include "baseline_controller_logic.hpp"
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>

int checks = 0;
void expect(bool okay, const char *name) { ++checks; if (!okay) throw std::runtime_error(name); }
bool near(float a, float b) { return std::fabs(a - b) < .0001f; }
void cadence_checks() {
    using namespace qpro_controller;
    for (auto mode : {Mode::Trackpad, Mode::Joystick, Mode::Swipe})
        for (float smoothing : {0.f, 18.f}) for (int rate : {60, 90, 120, 144}) {
            Settings settings; settings.mode = mode; settings.smoothing_ms = smoothing;
            ContactFilter rendered, reference; PacketLease lease;
            qpro_controller_baseline::ContactFilter source_swipe;
            qpro_controller_baseline::Settings source_settings;
            source_settings.mode = qpro_controller_baseline::Mode::Swipe;
            source_settings.smoothing_ms = smoothing;
            float swipe_x = 0, swipe_y = 0;
            QptpPacket source{}, current{}; Output expected;
            int previous = -1;
            for (int frame = 0; frame <= rate * 2; ++frame) {
                int index = frame * 60 / rate;
                uint64_t now = 1000000000ULL + uint64_t(frame) * 1000000000ULL / rate;
                if (index != previous) {
                    source.sequence = index + 1;
                    source.monotonic_ns = 5000000000ULL + uint64_t(index) * 1000000000ULL / 60;
                    source.sides[0] = {QPTP_VALID | QPTP_CONTACT, float(index) * .4f / 60, -.1f, .8f, 44};
                    expect(lease.accept(source, now), "new reader sample accepted during cadence replay");
                    expected = reference.step(source.sides[0], source.monotonic_ns, settings);
                    if (mode == Mode::Swipe) {
                        auto raw = source_swipe.step(source.sides[0], source.monotonic_ns, source_settings);
                        expected = {raw.x, raw.y, raw.force, raw.touch, raw.click};
                    }
                    previous = index;
                } else expect(!lease.accept(source, now), "duplicate cannot advance reader revision");
                uint64_t revision, epoch;
                expect(lease.current(now, current, &revision, &epoch), "render uses a fresh local lease");
                auto output = rendered.step_packet(current.sides[0], now, settings,
                    current.monotonic_ns, revision, epoch);
                if (mode == Mode::Swipe) {
                    float dt = frame ? float(uint64_t(frame) * 1000000000ULL / rate
                        - uint64_t(frame - 1) * 1000000000ULL / rate) / 1000000.f : 1000.f / 60;
                    float blend = -std::expm1(-dt / 12.f);
                    swipe_x += blend * (expected.x - swipe_x);
                    swipe_y += blend * (expected.y - swipe_y);
                }
                expect(near(output.x, mode == Mode::Swipe ? swipe_x : expected.x)
                    && near(output.y, mode == Mode::Swipe ? swipe_y : expected.y)
                    && near(output.force, expected.force) && output.touch == expected.touch
                    && output.click == expected.click,
                    "source-sampled modes stay exact and Swipe follows its local-render-time recurrence");
                if (mode == Mode::Swipe && index >= 60)
                    expect(near(output.x, .088f), "constant source motion produces constant swipe without duplicate pulses");
            }
        }
    for (int rate : {60, 90, 120, 144}) {
        Settings settings; settings.mode = Mode::Swipe; settings.smoothing_ms = 0;
        ContactFilter filter;
        QptpSide side{QPTP_VALID | QPTP_CONTACT, 0, 0, .8f, 44};
        filter.step_packet(side, 1000000000, settings, 5000000000, 1, 1);
        side.x = .1f;
        auto moving = filter.step_packet(side, 1050000000, settings, 5050000000, 2, 1);
        float expected = .4268f * (-std::expm1(-50.f / 12.f));
        expect(near(moving.x, expected), "source-interval velocity enters the bounded render-time filter");
        auto held = filter.step_packet(side, 1055000000, settings, 5050000000, 2, 1);
        expected += (-std::expm1(-5.f / 12.f)) * (.4268f - expected);
        expect(near(held.x, expected), "held duplicate advances render smoothing without erasing source velocity");
        side.flags = QPTP_VALID;
        auto released = filter.step_packet(side, 1060000000, settings, 5060000000, 3, 1);
        float raw_velocity = .4268f * std::exp(-5.f / settings.glide_ms);
        expected += (-std::expm1(-5.f / 12.f)) * (raw_velocity - expected);
        for (int frame = 1; frame <= rate / 3; ++frame) {
            float dt = float(uint64_t(frame) * 1000000000ULL / rate
                - uint64_t(frame - 1) * 1000000000ULL / rate) / 1000000.f;
            raw_velocity *= std::exp(-dt / settings.glide_ms);
            expected += (-std::expm1(-dt / 12.f)) * (raw_velocity - expected);
            released = filter.step_packet(side, 1060000000ULL + uint64_t(frame) * 1000000000ULL / rate,
                settings, 5060000000, 3, 1);
        }
        expect(near(released.x, expected) && released.touch && !released.click && !released.force,
            "permitted glide retains its elapsed-time decay through the render-time recurrence");
        side.flags = QPTP_VALID | QPTP_CONTACT; side.x = .8f;
        auto restarted = filter.step_packet(side, 1500000000, settings, 1000, 4, 2);
        expect(restarted.touch && restarted.click && !restarted.x, "expired reader epoch cannot inherit swipe velocity");
        settings.mode = Mode::Joystick;
        auto anchored = filter.step_packet(side, 1505000000, settings, 1000, 4, 2);
        expect(anchored.touch && anchored.click && !anchored.x && !anchored.y, "mode switch reanchors the current packet");
        settings.mode = Mode::Trackpad;
        expect(near(filter.step_packet(side, 1510000000, settings, 1000, 4, 2).x, .8f),
            "mode switch restores absolute position without waiting for new data");
        MouseMotion mouse;
        expect(mouse.step(anchored, true, 900).down, "packet click can press the desktop button");
        filter.clear();
        auto neutral = filter.step_packet(QptpSide{}, 1710000000, settings, 1000, 4, 2);
        expect(!neutral.touch && !neutral.click && !neutral.x && !neutral.force,
            "stale or invalid input returns neutral and clears filter state");
        expect(mouse.step(neutral, true, 900).up, "stale input releases the desktop button");
    }
    Settings settings; settings.mode = Mode::Swipe; settings.smoothing_ms = 0;
    ContactFilter stalled;
    QptpSide side{QPTP_VALID | QPTP_CONTACT, 0, 0, .8f, 44};
    stalled.step_packet(side, 1000000000, settings, 5000000000, 1, 1);
    side.x = .1f;
    float raw_velocity = .22f * .097f / .15f;
    float expected = raw_velocity * (-std::expm1(-10.f / 12.f));
    expect(near(stalled.step_packet(side, 1010000000, settings, 5150000000, 2, 1).x, expected),
        "longer source interval stays bounded before render smoothing");
    side.flags = QPTP_VALID;
    stalled.step_packet(side, 1020000000, settings, 5160000000, 3, 1);
    raw_velocity *= std::exp(-10.f / settings.glide_ms);
    expected += (-std::expm1(-10.f / 12.f)) * (raw_velocity - expected);
    auto glided = stalled.step_packet(side, 1170000000, settings, 5160000000, 3, 1);
    raw_velocity *= std::exp(-150.f / settings.glide_ms);
    expected += (-std::expm1(-150.f / 12.f)) * (raw_velocity - expected);
    expect(near(glided.x, expected), "fresh lease glide and render smoothing account for a150ms render stall");
    expect(near(stalled.step_packet(side, 1170000000, settings, 5160000000, 3, 1).x, glided.x),
        "same local frame time cannot advance glide");
    for (auto mode : {Mode::Trackpad, Mode::Joystick, Mode::Swipe, Mode::Mouse})
        for (auto selection : {ControllerSide::Both, ControllerSide::Left, ControllerSide::Right}) {
            Settings selected; selected.mode = mode; selected.side = selection; selected.smoothing_ms = 0;
            ContactFilter filters[2];
            QptpSide contact{QPTP_VALID | QPTP_CONTACT, .1f, .2f, .8f, 44};
            for (int index = 0; index < 2; ++index) {
                filters[index].step_packet(contact, 1000000000, selected, 5000000000, 1, 1, index);
                contact.x = .2f;
                auto value = filters[index].step_packet(contact, 1020000000, selected, 5020000000, 2, 1, index);
                expect(value.touch == selected.applies_to(index) && value.click == selected.applies_to(index),
                    "all mode outputs honor left/right/both selection");
                selected.side = index == 0 ? ControllerSide::Right : ControllerSide::Left;
                auto excluded = filters[index].step_packet(contact, 1030000000, selected, 5020000000, 2, 1, index);
                expect(!excluded.x && !excluded.y && !excluded.force && !excluded.touch && !excluded.click,
                    "selection change cancels all mode output including swipe inertia");
                selected.side = selection;
                contact.x = .1f;
            }
        }
}
void accuracy_checks() {
    using namespace qpro_controller;
    constexpr uint64_t local = 1000000000ULL, source = 5000000000ULL;
    for (auto mode : {Mode::Trackpad, Mode::Joystick, Mode::Swipe, Mode::Mouse}) {
        Settings settings; settings.mode = mode;
        ContactFilter filter;
        QptpSide contact{QPTP_VALID | QPTP_CONTACT, .2f, -.1f, .8f, 44};
        auto first = filter.step_packet(contact, local, settings, source, 1, 1);
        for (int index = 1; index <= 90; ++index) {
            auto stable = filter.step_packet(contact, local + index * 11111111ULL,
                settings, source + index * 11111111ULL, index + 1, 1);
            expect(near(first.x, stable.x) && near(first.y, stable.y)
                && stable.touch && stable.click, "stationary contact never manufactures motion or drops pressure");
        }
        contact.flags = QPTP_VALID;
        auto release = filter.step_packet(contact, local + 1100000000ULL, settings,
            source + 1100000000ULL, 92, 1);
        expect(!release.touch && !release.click && !release.force && !release.x && !release.y,
            "stationary high-pressure contact releases every mode immediately");
        contact.flags = QPTP_VALID | QPTP_CONTACT; contact.x = -.8f;
        auto recontact = filter.step_packet(contact, local + 1110000000ULL, settings,
            source + 1110000000ULL, 93, 1);
        expect(recontact.touch && recontact.click && (mode == Mode::Trackpad || mode == Mode::Mouse
            ? near(recontact.x, -.8f) : !recontact.x), "new contact anchors at a distant point without old drag velocity");
    }
    // The current reader's two coordinate bytes have 255 intervals. Include
    // that resolution instead of claiming extra physical sensor precision.
    constexpr float quantum = 2.f / 255;
    Settings joystick; joystick.mode = Mode::Joystick; joystick.smoothing_ms = 0;
    ContactFilter relative;
    QptpSide contact{QPTP_VALID | QPTP_CONTACT, 0, 0, .8f, 44};
    relative.step(contact, local, joystick);
    for (int index = 1; index <= 120; ++index) {
        contact.x = index % 2 ? quantum : -quantum;
        contact.y = index % 2 ? -quantum : quantum;
        auto value = relative.step(contact, local + index * 10000000ULL, joystick);
        expect(!value.x && !value.y && value.click, "one quantized bin of centered joystick noise stays neutral");
    }
    for (float x : {-.6f, -.3f, -.1f, -.02f, .02f, .1f, .3f, .6f})
        for (float y : {-.6f, -.3f, -.1f, 0.f, .1f, .3f, .6f}) {
        ContactFilter direction; contact.x = contact.y = 0; direction.step(contact, local, joystick);
        contact.x = x; contact.y = y;
        auto value = direction.step(contact, local + 20000000, joystick);
        float raw_x = x / joystick.joystick_span, raw_y = y / joystick.joystick_span;
        expect(std::fabs(value.x * raw_y - value.y * raw_x) < .00001f,
            "joystick soft deadband preserves cardinal and diagonal direction");
        expect(std::fabs(value.x) <= std::fabs(raw_x) + .00001f && std::fabs(value.y) <= std::fabs(raw_y) + .00001f,
            "joystick soft deadband never amplifies the requested displacement");
        if (std::fabs(x) == .6f || std::fabs(y) == .6f)
            expect(near(std::max(std::fabs(value.x), std::fabs(value.y)), 1),
            "joystick keeps full travel at every square edge");
    }
    contact.x = contact.y = 0;
    for (int rate : {40, 60, 90}) for (float increment : {.001f, .002f, .003f, quantum}) {
        Settings swipe; swipe.mode = Mode::Swipe;
        contact.x = -.75f;
        ContactFilter drag; drag.step(contact, local, swipe, true, source);
        double distance = 0;
        for (int index = 1; index <= rate * 2; ++index) {
            contact.x = -.75f + increment * index;
            auto value = drag.step(contact, local + uint64_t(index) * 1000000000ULL / rate,
                swipe, true, source + uint64_t(index) * 1000000000ULL / rate);
            distance += double(value.x) / swipe.swipe_gain / rate;
            expect(value.x >= -.000001f && !value.y, "small coherent swipes never reverse or create cross-axis movement");
            if (index >= rate / 2) expect(std::fabs(value.x - increment * rate * swipe.swipe_gain) < .0001f,
                "gentle accumulated swipes retain the requested steady speed at every reader rate");
        }
        float speed = increment * rate;
        expect(std::fabs(distance - increment * rate * 2) <= .0031 + speed * .032,
            "swipe displacement loss is bounded by the allowance plus source and12ms render-filter lag");
        contact.x = 0;
    }
    Settings swipe; swipe.mode = Mode::Swipe;
    ContactFilter jitter; jitter.step(contact, local, swipe, true, source);
    for (int index = 1; index <= 120; ++index) {
        contact.x = index % 2 ? .002f : -.002f; contact.y = 0;
        auto value = jitter.step(contact, local + index * 10000000ULL,
            swipe, true, source + index * 10000000ULL);
        expect(!value.x && !value.y, "stationary sub-allowance swipe jitter produces no velocity");
    }
    ContactFilter rapid; swipe.smoothing_ms = 0; contact.x = contact.y = 0;
    rapid.step(contact, local, swipe, true, source); contact.x = .00301f;
    auto tiny_interval = rapid.step(contact, local + 10000000, swipe, true, source + 1);
    expect(tiny_interval.x > 0 && tiny_interval.x < .003f && !tiny_interval.y,
        "a sub-millisecond source interval cannot amplify tiny movement into a full swipe");
    // Reader and render rates differ. Repeat the same packet between samples;
    // source-time filtering must be independent of the number of render calls.
    for (auto mode : {Mode::Trackpad, Mode::Joystick, Mode::Swipe})
        for (int source_rate : {40, 60, 90}) for (int render_rate : {90, 120, 144}) {
            Settings settings; settings.mode = mode;
            ContactFilter rendered, sampled; Output expected;
            qpro_controller_baseline::ContactFilter source_swipe;
            qpro_controller_baseline::Settings source_settings;
            source_settings.mode = qpro_controller_baseline::Mode::Swipe;
            float swipe_x = 0, swipe_y = 0;
            int previous = -1;
            for (int frame = 0; frame <= render_rate; ++frame) {
                int index = frame * source_rate / render_rate;
                contact.x = std::round(float(index) * .4f / source_rate / quantum) * quantum;
                uint64_t clock = source + uint64_t(index) * 1000000000ULL / source_rate;
                if (index != previous) {
                    expected = sampled.step(contact, clock, settings, true, clock);
                    if (mode == Mode::Swipe) {
                        auto raw = source_swipe.step(contact, clock, source_settings, true, clock);
                        expected = {raw.x, raw.y, raw.force, raw.touch, raw.click};
                    }
                    previous = index;
                }
                auto value = rendered.step_packet(contact, local + uint64_t(frame) * 1000000000ULL / render_rate,
                    settings, clock, index + 1, 1);
                if (mode == Mode::Swipe) {
                    float dt = frame ? float(uint64_t(frame) * 1000000000ULL / render_rate
                        - uint64_t(frame - 1) * 1000000000ULL / render_rate) / 1000000.f : 1000.f / 60;
                    float blend = -std::expm1(-dt / 12.f);
                    swipe_x += blend * (expected.x - swipe_x);
                    swipe_y += blend * (expected.y - swipe_y);
                }
                expect(near(value.x, mode == Mode::Swipe ? swipe_x : expected.x)
                    && near(value.y, mode == Mode::Swipe ? swipe_y : expected.y) && value.touch && value.click,
                    "quantized source-only modes stay identical and Swipe matches its render-time recurrence");
            }
        }
    for (auto mode : {Mode::Trackpad, Mode::Joystick, Mode::Swipe, Mode::Mouse})
        for (uint64_t next_clock : {source - 1, source, source + 200000000ULL, source + 900000000ULL}) {
            Settings settings; settings.mode = mode; settings.smoothing_ms = 0;
            ContactFilter gap; MouseMotion mouse;
            contact.x = .1f;
            auto initial = gap.step_packet(contact, local, settings, source, 1, 1);
            mouse.step(initial, true, 900);
            contact.x = -.9f;
            auto neutral = gap.step_packet(contact, local + 20000000, settings, next_clock, 2, 1);
            auto release = mouse.step(neutral, true, 900);
            expect(!neutral.touch && !neutral.click && !neutral.x && !neutral.y && !neutral.force
                && release.up && !release.dx && !release.dy,
                "source clock gaps and regressions release without inherited motion or clicks");
            auto renewed = gap.step_packet(contact, local + 40000000, settings, next_clock + 20000000, 3, 1);
            auto anchor = mouse.step(renewed, true, 900);
            expect(renewed.touch && anchor.down && !anchor.dx && !anchor.dy,
                "first healthy sample after a source discontinuity reanchors the mouse");
        }
    Settings settings; settings.smoothing_ms = 0;
    for (auto bad : {std::numeric_limits<float>::quiet_NaN(), std::numeric_limits<float>::infinity(), 1.01f}) {
        ContactFilter finite; MouseMotion mouse;
        contact.x = 0; auto valid = finite.step(contact, local, settings); mouse.step(valid, true, 900);
        contact.x = bad; auto invalid = finite.step(contact, local + 10000000, settings);
        expect(!invalid.touch && !invalid.click && !invalid.x && mouse.step(invalid, true, 900).up,
            "invalid direct sensor calls fail neutral instead of poisoning retained state");
        Output bad_cursor; bad_cursor.touch = bad_cursor.click = true; bad_cursor.x = bad;
        expect(!mouse.step(bad_cursor, true, 900).down, "mouse rejects nonfinite and out-of-range positions");
    }
    MouseMotion precise; Output cursor; cursor.touch = cursor.click = true;
    precise.step(cursor, true, 900);
    int pixels = 0;
    for (int index = 1; index <= 10000; ++index) {
        cursor.x = float(index) / 100000;
        auto move = precise.step(cursor, true, 900); pixels += move.dx;
        expect(move.dx >= 0 && !move.down && !move.up, "subpixel mouse motion never reverses or duplicates click edges");
    }
    expect(std::abs(pixels - 90) <= 1, "subpixel mouse motion accumulates to the expected total");
    auto invalid_scale = precise.step(cursor, true, std::numeric_limits<float>::infinity());
    expect(invalid_scale.up && !invalid_scale.dx && !invalid_scale.down,
        "invalid mouse scale releases safely before an unsafe integer conversion");
    StereoMouseMotion aggregate; std::array<Output, 2> values{};
    values[0].touch = values[0].click = values[1].touch = values[1].click = true;
    expect(aggregate.step(values, true, settings).down, "two valid mouse thumbs acquire one shared click");
    values[0].x = std::numeric_limits<float>::quiet_NaN();
    expect(!aggregate.step(values, true, settings).up && aggregate.pressed(),
        "one invalid thumb does not release the other valid thumb's shared click");
    values[1].y = std::numeric_limits<float>::infinity();
    expect(aggregate.step(values, true, settings).up && !aggregate.pressed(),
        "two invalid thumbs cannot retain the shared mouse click");
    expect(!aggregate.step(values, true, settings).up, "invalid shared mouse release stays idempotent");
    values = {}; values[0].touch = values[0].click = true;
    expect(aggregate.step(values, true, settings).down, "valid mouse recovers after invalid positions");
    settings.mouse_scale = std::numeric_limits<float>::quiet_NaN();
    expect(aggregate.step(values, true, settings).up && !aggregate.pressed(),
        "invalid scale also clears the shared click from validated child states");
}
void mouse_interpolation_checks() {
    using namespace qpro_controller;
    constexpr uint64_t local = 1000000000ULL, source = 5000000000ULL;
    Settings settings; settings.mode = Mode::Mouse; settings.smoothing_ms = 0;
    ContactFilter filter;
    QptpSide contact{QPTP_VALID | QPTP_CONTACT, 0, 0, .8f, 44};
    auto anchor = filter.step_packet(contact, local, settings, source, 1, 1);
    MouseMotion mouse; expect(mouse.step(anchor, true, 900).down, "mouse interpolation never delays initial click");
    contact.x = .2f; contact.y = -.1f;
    auto first = filter.step_packet(contact, local + 20000000, settings, source + 20000000, 2, 1);
    auto pixels = mouse.step(first, true, 900);
    float blend20 = -std::expm1(-20.f / 12.f);
    expect(near(first.x, .1f * blend20) && near(first.y, -.05f * blend20)
        && pixels.dx > 0 && pixels.dy > 0,
        "a source position step starts immediately through the two-tap and 12 ms filters");
    auto repeated_time = filter.step_packet(contact, local + 20000000, settings, source + 20000000, 2, 1);
    auto repeated_pixels = mouse.step(repeated_time, true, 900);
    expect(near(repeated_time.x, first.x) && near(repeated_time.y, first.y)
        && !repeated_pixels.dx && !repeated_pixels.dy,
        "repeated same-time render calls cannot advance interpolation");
    auto settled = filter.step_packet(contact, local + 30000000, settings, source + 20000000, 2, 1);
    auto remainder = mouse.step(settled, true, 900);
    float blend10 = -std::expm1(-10.f / 12.f);
    expect(near(settled.x, first.x + blend10 * (.2f - first.x))
        && near(settled.y, first.y + blend10 * (-.1f - first.y))
        && remainder.dx > 0 && remainder.dy > 0,
        "next render continues toward the held source point without a position jump");
    contact.force = .4f;
    auto released_click = filter.step_packet(contact, local + 40000000, settings, source + 40000000, 3, 1);
    expect(!released_click.click && mouse.step(released_click, true, 900).up,
        "interpolation never delays pressure release");
    contact.force = .8f;
    expect(mouse.step(filter.step_packet(contact, local + 50000000, settings, source + 50000000, 4, 1), true, 900).down,
        "interpolation never delays pressure press");
    contact.flags = QPTP_VALID;
    auto lift = filter.step_packet(contact, local + 60000000, settings, source + 60000000, 5, 1);
    expect(!lift.touch && !lift.click && !lift.x && !lift.y && mouse.step(lift, true, 900).up,
        "lift cancels interpolation and releases immediately");
    contact.flags = QPTP_VALID | QPTP_CONTACT; contact.x = -.8f; contact.y = .6f;
    auto reanchor = mouse.step(filter.step_packet(contact, local + 70000000, settings, source + 70000000, 6, 1), true, 900);
    expect(reanchor.down && !reanchor.dx && !reanchor.dy,
        "distant recontact never replays an unfinished movement");

    constexpr float quantum = 2.f / 255;
    for (int reader_rate : {40, 60, 90}) for (int render_rate : {90, 120, 144}) {
        Settings pointer_settings; pointer_settings.mode = Mode::Mouse;
        Settings target_settings = pointer_settings; target_settings.mode = Mode::Trackpad;
        ContactFilter pointer, target;
        MouseMotion movement, reference;
        Output target_value; float previous_x = 0, previous_y = 0;
        int previous_index = -1, total_x = 0, total_y = 0, expected_x = 0, expected_y = 0;
        float reference_x = 0, reference_y = 0;
        // Coherent quantized drags, a reversal and a one-second held settle.
        // EMA endpoints converge within coordinate/pixel tolerance, not in a
        // mathematically finite number of render frames.
        for (int frame = 0; frame <= render_rate * 3 + 2; ++frame) {
            int index = std::min(frame * reader_rate / render_rate, reader_rate * 3);
            int travel = index <= reader_rate ? index
                : index <= reader_rate * 2 ? reader_rate * 2 - index : 0;
            contact.x = -.4f + quantum * travel;
            contact.y = .2f - quantum * travel * .5f;
            contact.force = index % 20 < 10 ? .8f : .4f;
            uint64_t clock = source + uint64_t(index) * 1000000000ULL / reader_rate;
            uint64_t now = local + uint64_t(frame) * 1000000000ULL / render_rate;
            if (index != previous_index) {
                target_value = target.step_packet(contact, now, target_settings, clock, index + 1, 1);
                previous_index = index;
            }
            if (!frame) { previous_x = reference_x = target_value.x; previous_y = reference_y = target_value.y; }
            auto value = pointer.step_packet(contact, now, pointer_settings, clock, index + 1, 1);
            float tap_x = (target_value.x + previous_x) * .5f;
            float tap_y = (target_value.y + previous_y) * .5f;
            float prior_x = reference_x, prior_y = reference_y;
            if (frame) {
                uint64_t previous_now = local + uint64_t(frame - 1) * 1000000000ULL / render_rate;
                float blend = -std::expm1(-float(now - previous_now) / 1000000.f / 12.f);
                reference_x += blend * (tap_x - reference_x);
                reference_y += blend * (tap_y - reference_y);
            }
            expect(near(value.x, reference_x) && near(value.y, reference_y),
                "all reader/render cadences match the causal two-tap then 12 ms reference");
            expect(value.x >= std::min(prior_x, tap_x) - .000001f
                && value.x <= std::max(prior_x, tap_x) + .000001f
                && value.y >= std::min(prior_y, tap_y) - .000001f
                && value.y <= std::max(prior_y, tap_y) + .000001f,
                "mouse smoothing stays inside its prior-output and causal-target envelope at reversals");
            expect(value.touch == target_value.touch && value.click == target_value.click
                && value.force == target_value.force,
                "mouse interpolation keeps sample-gated touch, click and force unchanged");
            auto delta = movement.step(value, true, 900); total_x += delta.dx; total_y += delta.dy;
            auto original = reference.step(target_value, true, 900); expected_x += original.dx; expected_y += original.dy;
            if (frame == render_rate) {
                expect(total_x > quantum * reader_rate * 900 * .9f
                    && total_y > quantum * reader_rate * .5f * 900 * .9f,
                    "coherent fine movement accumulates through a full stroke before its reversal");
                int lag_bound = int(std::ceil(quantum * 900 * (1 + reader_rate * .012f))) + 1;
                expect(std::abs(total_x - expected_x) <= lag_bound
                    && std::abs(total_y - expected_y) <= lag_bound,
                    "steady drag lag stays within one source step plus the 12 ms filter bound");
            }
            if (index > reader_rate * 2 + reader_rate / 2)
                expect(std::fabs(value.x - target_value.x) < .00001f && std::fabs(value.y - target_value.y) < .00001f,
                    "a settled drag reaches the existing endpoint at every source/render rate");
            if (!frame) expect(value.x == target_value.x && value.y == target_value.y,
                "a first contact anchors every position-filter stage at its real source point");
            previous_x = target_value.x;
            previous_y = target_value.y;
        }
        expect(std::abs(total_x - expected_x) <= 1 && std::abs(total_y - expected_y) <= 1,
            "quantized slow drags and reversals preserve the original total pointer displacement");
    }
}
int main() {
    try {
        QptpPacket input{}; input.sequence = 42; input.monotonic_ns = 5000000000;
        input.sides[0] = { QPTP_VALID | QPTP_CONTACT, .5f, -.25f, .8f, 44 };
        input.sides[1] = { QPTP_VALID, -.5f, .25f, .1f, 12 };
        uint8_t wire[64]; qptp_encode(&input, wire); QptpPacket decoded;
        expect(wire[8] == 42 && wire[4] == 1 && wire[6] == 64, "wire little-endian header");
        expect(qptp_decode(wire, 64, &decoded) && decoded.sequence == 42, "round-trip packet");
        expect(decoded.sides[0].flags == 3 && near(decoded.sides[0].x, .5f) && near(decoded.sides[1].force, .1f), "independent sides");
        expect(!qptp_decode(wire, 63, &decoded) && !qptp_decode(wire, 65, &decoded), "exact frame length");
        wire[4] = 2; expect(!qptp_decode(wire, 64, &decoded), "unknown version rejected"); qptp_encode(&input, wire);
        wire[0] = 'X'; expect(!qptp_decode(wire, 64, &decoded), "wrong magic rejected"); qptp_encode(&input, wire);
        qptp_put_float(wire + 28, std::numeric_limits<float>::quiet_NaN()); expect(!qptp_decode(wire, 64, &decoded), "NaN rejected"); qptp_encode(&input, wire);
        qptp_put_float(wire + 40, 513); expect(!qptp_decode(wire, 64, &decoded), "size range bounded"); qptp_encode(&input, wire);
        qptp_put_u32(wire + 24, QPTP_CONTACT); expect(!qptp_decode(wire, 64, &decoded), "contact needs valid sample"); qptp_encode(&input, wire);
        qptp_put_u32(wire + 24, 8); expect(!qptp_decode(wire, 64, &decoded), "reserved flags rejected");

        using namespace qpro_controller;
        PacketLease lease;
        expect(lease.accept(input, 1000000000), "first packet accepted");
        expect(!lease.accept(input, 1000000001), "duplicate rejected");
        auto next = input; next.sequence++; next.monotonic_ns++;
        expect(lease.accept(next, 1010000000), "increasing packet accepted");
        uint64_t revision, epoch;
        expect(lease.current(1010000001, decoded, &revision, &epoch) && revision == 2 && epoch == 1,
            "active samples advance revision in one lease epoch");
        expect(!lease.accept(next, 1010000002) && lease.current(1010000003, decoded, &revision, &epoch)
            && revision == 2 && epoch == 1, "duplicates preserve sample revision and lease epoch");
        expect(lease.current(1209999999, decoded), "fresh packet available before lease boundary");
        expect(!lease.current(1210000000, decoded), "stale at200ms boundary");
        next.sequence = 1; next.monotonic_ns++;
        expect(lease.accept(next, 1210000001), "reader restart only after expiry and advancing source time");
        expect(lease.current(1210000001, decoded, &revision, &epoch) && revision == 3 && epoch == 2,
            "expired lease starts a new filter epoch");
        expect(!lease.accept(input, 1210000002), "old capture replay rejected after restart");
        expect(!lease.accept(next, 1410000001), "exact duplicate cannot renew expired lease");
        auto reboot = input; reboot.sequence = 1; reboot.monotonic_ns = 1000;
        expect(lease.accept(reboot, 1410000002), "new headset clock epoch allowed only after expiry");
        expect(lease.current(1410000002, decoded, &revision, &epoch) && revision == 4 && epoch == 3,
            "source clock restart starts a new filter epoch");
        reboot.sequence++; reboot.monotonic_ns--;
        expect(!lease.accept(reboot, 1410000003), "source clock cannot regress inside new active epoch");
        reboot.monotonic_ns = 1001;
        expect(lease.accept(reboot, 1410000004), "new epoch accepts increasing sequence and clock");
        lease.clear(); expect(!lease.current(1210000002, decoded), "shutdown clears lease");

        Settings settings; settings.smoothing_ms = 0;
        ContactFilter filter; uint64_t tick = 1000000000;
        auto output = filter.step(input.sides[0], tick, settings);
        expect(output.touch && output.click && near(output.x, .5f) && near(output.y, -.25f), "absolute position with pressure click");
        auto side = input.sides[0]; side.force = .5f;
        expect(filter.step(side, tick += 20000000, settings).click, "click stays below onset through hysteresis");
        side.force = .4f; expect(!filter.step(side, tick += 20000000, settings).click, "click releases below lower threshold");
        side.flags = QPTP_VALID; output = filter.step(side, tick += 20000000, settings);
        expect(!output.touch && !output.click && output.x == 0 && output.y == 0 && output.force == 0, "release returns neutral");
        side.flags = 3; settings.mode = Mode::Joystick; output = filter.step(side, tick += 20000000, settings);
        expect(output.x == 0 && output.y == 0, "relative joystick anchors initial touch");
        side.x = -1; output = filter.step(side, tick += 20000000, settings);
        expect(output.x == -1, "relative joystick clamps displacement");
        side.flags = 0; output = filter.step(side, tick += 20000000, settings);
        expect(!output.touch && !output.click && output.x == 0, "invalid sample cannot retain output");
        settings.mode = Mode::Swipe; side = input.sides[0]; filter.step(side, tick += 20000000, settings);
        side.x += .1f; output = filter.step(side, tick += 20000000, settings);
        expect(output.touch && output.x > 0, "swipe responds to movement");
        filter.clear(); output = filter.step(QptpSide{}, tick += 20000000, settings);
        expect(!output.touch && output.x == 0, "expiry clears swipe inertia");
        settings.mode = Mode::Trackpad; settings.ignore_resting_thumb = true; side.flags = 3; side.size = 120; side.force = .1f;
        expect(!filter.step(side, tick += 20000000, settings).touch, "optional broad resting contact rejected");
        side.force = .4f; expect(filter.step(side, tick += 20000000, settings).touch, "intentional pressure overrides resting filter");

        Configuration config;
        std::string nonce, request = std::string(health_request) + "0123456789abcdef0123456789abcdef";
        expect(decode_health_request(reinterpret_cast<const uint8_t *>(request.data()), request.size(), nonce)
            && nonce == "0123456789abcdef0123456789abcdef", "health sideband decodes a bounded token without changing QPTP");
        expect(!decode_health_request(reinterpret_cast<const uint8_t *>(request.data()), request.size() - 1, nonce),
            "truncated health request rejected");
        request.back() = 'G';
        expect(!decode_health_request(reinterpret_cast<const uint8_t *>(request.data()), request.size(), nonce),
            "health token cannot inject JSON or accept non-hex input");
        DriverHealth health; health.settings_revision = 3; health.applied_revision = 2;
        auto encoded = encode_health(health, nonce);
        expect(encoded.size() < 1024 && encoded.find("\"settingsRevision\":3,\"appliedRevision\":2") != std::string::npos
            && encoded.find("\"neutral\":false") != std::string::npos,
            "health distinguishes unapplied settings and does not initially claim neutral");
        MouseDelivery delivery;
        ComponentDelivery component;
        expect(!component.neutral(), "native component state starts unconfirmed");
        for (int index = 0; index < 5; ++index) {
            std::array<int, 5> errors{}; errors[index] = index + 1;
            component.complete({}, errors);
            expect(component.error() == index + 1 && !component.neutral(), "any rejected component prevents a neutral acknowledgement");
            component.complete({}, {});
            expect(!component.error() && component.neutral(), "successful neutral retries recover component delivery");
        }
        Output nonneutral; nonneutral.touch = true; component.complete(nonneutral, {});
        expect(!component.error() && !component.neutral(), "successful active components never claim neutral");
        auto failed_press = delivery.plan({}, true); expect(failed_press.down && !failed_press.up, "OS mouse press planned from accepted state");
        delivery.complete(failed_press, false, 5);
        expect(!delivery.pressed() && delivery.error() == 5 && delivery.plan({}, true).down,
            "rejected OS press remains visible and retries even without a new gesture edge");
        expect(!delivery.plan({}, false).up, "rejected press never releases a button it did not acquire");
        delivery.complete(delivery.plan({}, true), true, 0);
        expect(delivery.pressed() && !delivery.error() && !delivery.plan({}, true).down, "accepted press does not duplicate edges");
        auto failed_release = delivery.plan({}, false); delivery.complete(failed_release, false, 5);
        expect(delivery.pressed() && delivery.plan({}, false).up && delivery.error() == 5,
            "rejected release retains OS ownership and retries through Stop or stale frames");
        delivery.complete(delivery.plan({}, false), true, 0);
        expect(!delivery.pressed() && !delivery.error() && !delivery.plan({}, false).up, "successful retry clears held state exactly once");
        LatestConfiguration mailbox; Configuration received; bool valid = true; uint64_t seen = 0;
        expect(mailbox.take(seen, received, valid) && !valid && !received.enabled,
            "settings handoff starts disabled without a validated file");
        expect(!mailbox.take(seen, received, valid), "unchanged settings do not reapply on each render frame");
        Configuration first_settings; first_settings.enabled = true;
        expect(mailbox.publish(first_settings, true), "complete settings can be handed off");
        auto first_revision = mailbox.revision();
        expect(!mailbox.publish(first_settings, true) && mailbox.revision() == first_revision,
            "identical background reads do not grow the revision");
        auto last_settings = first_settings; last_settings.input.side = ControllerSide::Left;
        last_settings.input.mode = Mode::Mouse; last_settings.input.smoothing_ms = 17;
        expect(mailbox.publish(last_settings, true) && mailbox.take(seen, received, valid)
            && valid && same_configuration(received, last_settings),
            "render receives the latest complete settings without draining obsolete changes");
        expect(mailbox.publish(first_settings, false) && mailbox.take(seen, received, valid)
            && !valid && !received.enabled && received.input.mode == Mode::Trackpad,
            "invalid or missing settings fail closed instead of retaining enabled input");
        expect(mailbox.publish(last_settings, true) && mailbox.take(seen, received, valid)
            && valid && received.enabled, "a valid file recovers after an invalid read");
        ControllerDiscovery<64> covered;
        ControllerAvailability<64> availability;
        expect(availability.can_adopt(2, 100), "a newly discovered container can be adopted");
        availability.deactivated(2, 100);
        expect(!availability.can_adopt(2, 100) && !availability.can_adopt(2, 100),
            "readable cached properties cannot re-adopt a deactivated wrapper");
        availability.activated(2);
        expect(availability.can_adopt(2, 100), "a fresh wrapped activation permits reuse of its own container");
        availability.deactivated(2, 100);
        expect(availability.can_adopt(2, 101), "a different device container can reuse a deactivated index");
        expect(!availability.can_adopt(2, 0) && !availability.can_adopt(99, 100), "missing and invalid containers cannot be adopted");
        expect(covered.poll(false, 0) == std::array<bool, 64>{}, "disabled driver performs no discovery reads");
        auto initial_scan = covered.poll(true, 0);
        for (bool selected : initial_scan) expect(selected, "initial enable discovers every pre-existing index");
        covered.activated(2); covered.remember(2); covered.activated(6);
        expect(covered.poll(true, 124999999) == std::array<bool, 64>{}, "discovery work stays bounded between batches");
        auto pending = covered.poll(true, 125000000);
        expect(pending[6] && !pending[2], "hotplug with late role or profile properties remains retryable after Activate");
        covered.remember(6);
        for (int batch = 2; batch < 8; ++batch) covered.poll(true, batch * 125000000ULL);
        auto refresh = covered.poll(true, 1000000000);
        expect(refresh[2] && refresh[6], "successful late adoption enters the one-second known refresh");
        covered.remember(7); covered.activated(7);
        expect(covered.poll(true, 1125000000)[7], "new activation clears old index classification until role and profile are ready");
        covered.poll(false, 1125000001);
        auto resumed_scan = covered.poll(true, 1125000002);
        for (bool selected : resumed_scan) expect(selected, "re-enable rediscovers devices while disabled");
        ControllerDiscovery<8> empty, untouched;
        empty.activated(99); empty.remember(99); empty.poll(true, 0); untouched.poll(true, 0);
        expect(empty.poll(true, 1000000000) == untouched.poll(true, 1000000000), "invalid device indices cannot alter discovery");
        ControllerDiscovery<64> partial; partial.poll(true, 0); partial.activated(2); partial.remember(2);
        std::array<bool, 64> visited{};
        for (int batch = 1; batch <= 8; ++batch) {
            auto indices = partial.poll(true, batch * 125000000ULL); int count = 0;
            for (size_t id = 0; id < indices.size(); ++id) { visited[id] = visited[id] || indices[id]; count += indices[id]; }
            expect(count <= 9, "unknown pre-existing reconnect discovery never creates a full-slot render burst");
        }
        for (bool seen_index : visited) expect(seen_index, "all indices remain covered for unwrapped or migrated hotplug within the discovery cycle");
        expect(partial.poll(true, 1000000001) == std::array<bool, 64>{}, "discovery performs no busy scan between its scheduled batches");
        expect(parse_settings("{}", config) && !config.enabled && config.input.mode == Mode::Trackpad, "default disabled trackpad");
        expect(parse_settings("{\"enabled\":true,\"mode\":\"mouse\",\"smoothingMs\":0}", config) && config.enabled && config.input.mode == Mode::Mouse, "mouse needs explicit selection");
        expect(!parse_settings("{\"enabled\":true,\"mode\":\"unknown\"}", config), "unknown mode rejected");
        expect(parse_settings("{}", config) && config.input.side == ControllerSide::Both,
            "controller selection defaults to both");
        for (auto selection : {ControllerSide::Both, ControllerSide::Left, ControllerSide::Right}) {
            auto name = selection == ControllerSide::Both ? "both" : selection == ControllerSide::Left ? "left" : "right";
            expect(parse_settings(std::string("{\"side\":\"") + name + "\"}", config)
                && config.input.side == selection, "controller selection settings parsed");
            ContactFilter left, right; MouseMotion pointers[2];
            auto sample = input.sides[0];
            for (int index = 0; index < 2; ++index) {
                auto &selected_filter = index == 0 ? left : right;
                auto value = selected_filter.step_packet(sample, 1000000000, config.input, 5000000000, 1, 1, index);
                expect(value.touch == config.input.applies_to(index) && value.click == config.input.applies_to(index),
                    "only selected controllers produce extra thumb-rest touch and click");
                bool owns_mouse = config.input.applies_to(index);
                expect(pointers[index].step(value, owns_mouse, 900).down == owns_mouse,
                    "each selected controller can contribute a desktop click");
            }
            config.input.side = selection == ControllerSide::Left ? ControllerSide::Right : ControllerSide::Left;
            for (int index = 0; index < 2; ++index) {
                auto &selected_filter = index == 0 ? left : right;
                auto value = selected_filter.step_packet(sample, 1010000000, config.input, 5000000000, 1, 1, index);
                if (!config.input.applies_to(index)) {
                    expect(!value.touch && !value.click && !value.x && !value.y && !value.force,
                        "excluded controller returns neutral immediately on selection change");
                    auto released = pointers[index].step(value, false, 900);
                    bool was_selected = selection == ControllerSide::Both
                        || (selection == ControllerSide::Left && index == 0)
                        || (selection == ControllerSide::Right && index == 1);
                    expect(!released.down && !released.dx && !released.dy && released.up == was_selected,
                        "excluded controller cannot emit desktop motion or presses");
                }
            }
        }
        expect(!parse_settings("{\"side\":\"any\"}", config)
            && !parse_settings("{\"side\":\"left\",\"side\":\"right\"}", config), "invalid and duplicate side settings rejected");
        StereoMouseMotion shared_mouse; Settings mouse_settings; mouse_settings.mouse_scale = 1000;
        std::array<Output, 2> thumbs{}; thumbs[0].touch = thumbs[1].touch = true;
        expect(shared_mouse.step(thumbs, true, mouse_settings).dx == 0, "both mouse thumbs independently anchor");
        thumbs[0].x = .25f; thumbs[1].x = .5f; thumbs[0].y = .25f; thumbs[1].y = -.25f;
        auto combined = shared_mouse.step(thumbs, true, mouse_settings);
        expect(combined.dx == 750 && combined.dy == 0 && !combined.down && !combined.up,
            "both selected thumbs sum relative desktop motion");
        thumbs[0].click = true;
        expect(shared_mouse.step(thumbs, true, mouse_settings).down, "either selected thumb can press the shared desktop button");
        thumbs[1].click = true;
        auto second_press = shared_mouse.step(thumbs, true, mouse_settings);
        expect(!second_press.down && !second_press.up, "second thumb does not duplicate the global button press");
        thumbs[0].click = false; thumbs[0].touch = false;
        expect(!shared_mouse.step(thumbs, true, mouse_settings).up, "one thumb release preserves the other held desktop click");
        expect(!shared_mouse.release(0).up && shared_mouse.release(1).up,
            "controller deactivation releases only its shared click contribution");
        expect(!shared_mouse.release(1).up, "shared desktop release is idempotent");
        mouse_settings.side = ControllerSide::Left;
        auto right_excluded = shared_mouse.step(thumbs, true, mouse_settings);
        expect(!right_excluded.down && !right_excluded.dx && !right_excluded.dy,
            "left-only mouse cannot use the right thumb");
        thumbs[0].touch = thumbs[0].click = true;
        expect(shared_mouse.step(thumbs, true, mouse_settings).down, "left-only mouse can press from the left");
        expect(shared_mouse.step({}, false, mouse_settings).up, "mode or side change resets the shared desktop button once");
        mouse_settings.side = ControllerSide::Right;
        auto right_anchor = shared_mouse.step(thumbs, true, mouse_settings);
        expect(right_anchor.down && !right_anchor.dx && !right_anchor.dy,
            "new selected mouse side anchors without stale movement");
        expect(shared_mouse.step({}, false, mouse_settings).up && !shared_mouse.step({}, false, mouse_settings).up,
            "Stop or expired input releases the shared click exactly once");
        ContactFilter disconnected; mouse_settings.side = ControllerSide::Both; mouse_settings.mode = Mode::Mouse;
        auto retained = input.sides[0];
        thumbs = {};
        thumbs[0] = disconnected.step_packet(retained, 1000000000, mouse_settings, 5000000000, 1, 1, 0, true);
        expect(shared_mouse.step(thumbs, true, mouse_settings).down, "adopted controller can click from fresh data");
        expect(shared_mouse.release(0).up, "deactivated controller releases its click contribution");
        thumbs[0] = disconnected.step_packet(retained, 1010000000, mouse_settings, 5000000000, 1, 1, 0, false);
        auto absent = shared_mouse.step(thumbs, true, mouse_settings);
        expect(!thumbs[0].touch && !thumbs[0].click && !absent.down && !absent.dx && !absent.dy,
            "retained valid packet cannot revive a deactivated controller's mouse input");
        thumbs[0] = disconnected.step_packet(retained, 1020000000, mouse_settings, 5000000000, 1, 1, 0, true);
        auto adopted = shared_mouse.step(thumbs, true, mouse_settings);
        expect(adopted.down && !adopted.dx && !adopted.dy,
            "new adoption can reanchor the still-fresh packet without old cursor motion");
        expect(!parse_settings("{\"clickOn\":0.3,\"clickOff\":0.4}", config), "reversed click limits rejected");
        expect(!parse_settings("{\"enabled\":true,\"enabled\":false}", config), "duplicate key rejected");
        expect(!parse_settings("{\"glideMs\":nan}", config), "nonfinite setting rejected");
        expect(!parse_settings("{\"mode\":\"mouse\"}junk", config), "trailing content rejected");
        MouseMotion mouse; Output cursor; cursor.touch = cursor.click = true; cursor.x = .1f;
        auto move = mouse.step(cursor, false, 900); expect(!move.down && move.dx == 0, "mouse default cannot emit movement");
        move = mouse.step(cursor, true, 900); expect(move.down && move.dx == 0, "mouse first touch anchors and presses");
        cursor.x = .2f; move = mouse.step(cursor, true, 900); expect(move.dx >= 89 && !move.down, "mouse movement only after anchor");
        move = mouse.step({}, false, 900); expect(move.up && !move.down && !move.dx, "stop releases mouse button");
        expect(!mouse.step({}, false, 900).up, "shutdown release is idempotent");
        cadence_checks();
        accuracy_checks();
        mouse_interpolation_checks();
        std::cout << "PASS " << checks << " controller packet/contact/settings/shutdown checks\n"; return 0;
    } catch (const std::exception &error) { std::cerr << "FAIL " << error.what() << '\n'; return 1; }
}
