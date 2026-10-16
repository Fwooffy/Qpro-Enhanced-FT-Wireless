#include "../settings.hpp"
#include "baseline_controller_logic.hpp"
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <vector>

using namespace qpro_controller;
namespace baseline = qpro_controller_baseline;
int checks = 0;
void expect(bool value, const char *name) { ++checks; if (!value) throw std::runtime_error(name); }
bool same(const Output &a, const baseline::Output &b) {
    return a.x == b.x && a.y == b.y && a.force == b.force && a.touch == b.touch && a.click == b.click;
}
Output convert(const baseline::Output &b) { return {b.x, b.y, b.force, b.touch, b.click}; }

void unchanged_modes() {
    for (int mode : {0, 1}) for (int rate : {90, 120, 144}) for (int source_rate : {40, 60, 90}) {
        Settings settings; settings.mode = static_cast<Mode>(mode);
        baseline::Settings original; original.mode = static_cast<baseline::Mode>(mode);
        ContactFilter changed; baseline::ContactFilter before;
        for (int frame = 0; frame < rate * 4; ++frame) {
            int index = frame * source_rate / rate;
            QptpSide sample{uint32_t(QPTP_VALID | (index % 29 < 25 ? QPTP_CONTACT : 0)),
                std::sin(float(index) * .09f) * .8f, std::cos(float(index) * .12f) * .7f,
                index % 17 < 8 ? .8f : .4f, 44};
            uint64_t now = 1000000000ULL + uint64_t(frame) * 1000000000ULL / rate;
            uint64_t clock = 5000000000ULL + uint64_t(index) * 1000000000ULL / source_rate;
            expect(same(changed.step_packet(sample, now, settings, clock, index + 1, 1),
                before.step_packet(sample, now, original, clock, index + 1, 1)),
                "Trackpad and Joystick are bit-identical at every render frame");
        }
    }
}

void release_and_time_checks() {
    constexpr uint64_t start = 1000000000ULL, source = 5000000000ULL;
    Settings settings; settings.mode = Mode::Mouse; settings.smoothing_ms = 0;
    for (int cause = 0; cause < 8; ++cause) {
        ContactFilter filter; MouseMotion pointer;
        QptpSide sample{QPTP_VALID | QPTP_CONTACT, -.5f, .1f, .8f, 44};
        pointer.step(filter.step_packet(sample, start, settings, source, 1, 1, 0), true, 900);
        sample.x = .7f; sample.y = -.8f;
        auto moving = filter.step_packet(sample, start + 10000000, settings, source + 10000000, 2, 1, 0);
        expect(moving.touch && moving.x < .7f, "release cases begin with unfinished position motion");
        pointer.step(moving, true, 900);
        Settings change = settings; auto changed_sample = sample;
        uint64_t clock = source + 20000000, epoch = 1;
        bool available = true;
        if (cause == 0) changed_sample.flags = QPTP_VALID;
        if (cause == 1) changed_sample.flags = 0;
        if (cause == 2) available = false;
        if (cause == 3) change.side = ControllerSide::Right;
        if (cause == 4) ++epoch;
        if (cause == 5) clock = source + 210000000;
        if (cause == 6) clock = source;
        if (cause == 7) { change.mode = Mode::Trackpad; changed_sample.flags = 0; }
        auto stopped = filter.step_packet(changed_sample, start + 20000000, change, clock, 3, epoch, 0, available);
        auto release = pointer.step(stopped, true, 900);
        if (cause == 4) {
            // A new live epoch can anchor immediately, so no stale movement
            // is allowed even if the refreshed contact remains pressed.
            expect(stopped.x == sample.x && stopped.y == sample.y,
                "new lease epoch anchors at current source without carrying a filtered tail");
        } else expect(!stopped.touch && !stopped.click && stopped.x == 0 && stopped.y == 0
                && release.up && !release.dx && !release.dy,
                "lift, invalid, unavailable, excluded side, gap and mode changes release without a tail");
        filter.clear(); pointer = MouseMotion{}; sample.x = -.9f; sample.y = .9f;
        auto reanchor = pointer.step(filter.step_packet(sample, start + 30000000, settings,
            source + 30000000, 4, 2, 0), true, 900);
        expect(!reanchor.dx && !reanchor.dy, "fresh recontact anchors without replaying pending motion");
    }
    ContactFilter filter; QptpSide sample{3, 0, 0, .8f, 44};
    filter.step_packet(sample, start, settings, source, 1, 1);
    sample.x = 1;
    filter.step_packet(sample, start + 10000000, settings, source + 10000000, 2, 1);
    auto held = filter.step_packet(sample, start + 20000000, settings, source + 10000000, 2, 1);
    auto split = filter;
    auto one = filter.step_packet(sample, start + 40000000, settings, source + 10000000, 2, 1);
    split.step_packet(sample, start + 30000000, settings, source + 10000000, 2, 1);
    auto two = split.step_packet(sample, start + 40000000, settings, source + 10000000, 2, 1);
    expect(std::fabs(one.x - two.x) < 1e-6f, "final held EMA uses elapsed time rather than number of render calls");
    float prior = held.x;
    for (int frame = 3; frame < 30; ++frame) {
        auto next = filter.step_packet(sample, start + 40000000 + uint64_t(frame) * 10000000,
            settings, source + 10000000, 2, 1);
        expect(next.x >= prior && next.x <= 1, "held position settles monotonically without overshoot"); prior = next.x;
    }
    expect(std::fabs(prior - 1) < 1e-6f, "held final stage converges within explicit coordinate tolerance");
}

std::vector<QptpPacket> read_csv(const char *path) {
    std::ifstream file(path); if (!file) throw std::runtime_error("missing capture");
    std::string line; std::getline(file, line); std::vector<QptpPacket> result;
    while (std::getline(file, line)) {
        std::stringstream stream(line); std::array<std::string, 9> fields;
        for (auto &field : fields) if (!std::getline(stream, field, ',')) throw std::runtime_error("invalid capture row");
        QptpPacket packet{}; packet.sequence = result.size() + 1; packet.monotonic_ns = std::stoull(fields[0]);
        for (int side = 0; side < 2; ++side) { int offset = side ? 5 : 1;
            packet.sides[side] = {uint32_t(std::stoul(fields[offset])), std::stof(fields[offset + 1]),
                std::stof(fields[offset + 2]), std::stof(fields[offset + 3]), 44};
            expect(qptp_side_valid(&packet.sides[side]), "recorded side is protocol-valid"); }
        if (!result.empty()) expect(packet.monotonic_ns > result.back().monotonic_ns, "recorded source clock increases");
        result.push_back(packet);
    }
    expect(!result.empty(), "recorded capture is nonempty"); return result;
}
struct Metrics { int x = 0, y = 0, down = 0, up = 0; double peak = 0, change = 0; int frames = 0; };
void replay(const char *path) {
    auto packets = read_csv(path);
    for (int rate : {90, 120, 144}) {
        Settings settings; settings.mode = Mode::Mouse;
        baseline::Settings original; original.mode = baseline::Mode::Mouse;
        ContactFilter filtered[2]; baseline::ContactFilter old[2]; PacketLease lease;
        StereoMouseMotion mouse, old_mouse; Metrics result[2]; MouseUpdate previous[2];
        size_t next = 0; uint64_t origin = packets.front().monotonic_ns;
        uint64_t duration = packets.back().monotonic_ns - origin;
        unsigned frames = unsigned(duration * rate / 1000000000ULL) + rate * 15 / 100;
        for (unsigned frame = 0; frame <= frames; ++frame) {
            uint64_t elapsed = uint64_t(frame) * 1000000000ULL / rate, now = 1000000000ULL + elapsed;
            while (next < packets.size() && packets[next].monotonic_ns - origin <= elapsed) {
                expect(lease.accept(packets[next], 1000000000ULL + packets[next].monotonic_ns - origin),
                    "recorded packet accepted in local arrival order"); ++next;
            }
            QptpPacket current{}; uint64_t revision = 0, epoch = 0;
            bool fresh = lease.current(now, current, &revision, &epoch); std::array<Output, 2> outputs{}, before{};
            for (int side = 0; side < 2; ++side) if (fresh) {
                outputs[side] = filtered[side].step_packet(current.sides[side], now, settings,
                    current.monotonic_ns, revision, epoch, side);
                before[side] = convert(old[side].step_packet(current.sides[side], now, original,
                    current.monotonic_ns, revision, epoch, side));
                expect(outputs[side].touch == before[side].touch && outputs[side].click == before[side].click
                    && outputs[side].force == before[side].force, "recorded pressure, contact and click edges are unchanged");
            } else { filtered[side].clear(); old[side].clear(); }
            MouseUpdate changes[2] = {old_mouse.step(before, fresh, settings), mouse.step(outputs, fresh, settings)};
            for (int index = 0; index < 2; ++index) {
                auto &m = result[index]; auto change = changes[index];
                m.x += change.dx; m.y += change.dy; m.down += change.down; m.up += change.up;
                if (elapsed <= duration) {
                    m.peak = std::max(m.peak, std::hypot(double(change.dx), double(change.dy)));
                    m.change += std::hypot(double(change.dx - previous[index].dx), double(change.dy - previous[index].dy));
                    ++m.frames;
                }
                previous[index] = change;
            }
        }
        expect(result[0].down == result[1].down && result[0].up == result[1].up, "recorded mouse click transitions match");
        expect(std::abs(result[0].x - result[1].x) <= 1 && std::abs(result[0].y - result[1].y) <= 1,
            "recorded held-settled source-filtered endpoint matches within one pixel rounding");
        expect(result[1].peak <= result[0].peak + 1 && result[1].change <= result[0].change + 1,
            "recorded Mouse peak and mean frame step change do not increase");
        std::cout << path << " @" << rate << "Hz peak " << result[0].peak << " -> " << result[1].peak
            << "; mean frame-step change " << result[0].change / result[0].frames << " -> "
            << result[1].change / result[1].frames << "; net " << result[1].x << ',' << result[1].y << '\n';
    }
}
int main(int argc, char **argv) {
    try { unchanged_modes(); release_and_time_checks();
        std::cout << std::setprecision(9);
        for (int index = 1; index < argc; ++index) replay(argv[index]);
        std::cout << "PASS " << checks << " mouse motion regression checks\n"; return 0;
    } catch (const std::exception &error) { std::cerr << "FAIL after " << checks << ": " << error.what() << '\n'; return 1; }
}
