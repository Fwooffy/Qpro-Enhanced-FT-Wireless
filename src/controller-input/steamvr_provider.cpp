#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wunused-parameter"
#include "third_party/openvr/openvr_driver.h"
#pragma clang diagnostic pop
#include "driver_health.hpp"
#include "dashboard_scroll.hpp"
#include <array>
#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

namespace {
using namespace vr;
using namespace qpro_controller;
constexpr char profile[] = "{qpro_controller}/input/quest_pro_touchpad.json";
uint64_t clock_ns() {
    return (uint64_t)std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}
void log(const std::string &text) { VRDriverLog()->Log(("[qpro-controller] " + text).c_str()); }
struct Controls {
    PropertyContainerHandle_t container = 0;
    TrackedDeviceIndex_t device = k_unTrackedDeviceIndexInvalid;
    std::array<VRInputComponentHandle_t, 5> handles{};
    std::array<VRInputComponentHandle_t, 3> scroll_handles{};
    std::string original_profile;
    ContactFilter filter;
    ComponentDelivery delivery;
    DashboardScrollDelivery scroll_delivery;
    // Report actual delivery once per controller and selected mode.
    bool scroll_motion_logged = false;
};
std::mutex state_mutex;
std::array<Controls, 2> hands;
std::array<int, 2> input_creation_errors{};
PacketLease lease;
Configuration configuration;
LatestConfiguration latest_configuration;
uint64_t applied_configuration = 0;
bool settings_valid = false;
ControllerDiscovery<k_unMaxTrackedDeviceCount> discovery;
ControllerAvailability<k_unMaxTrackedDeviceCount> availability;
StereoMouseMotion desktop_mouse;
MouseDelivery mouse_delivery;
uint64_t restore_errors = 0;
uint64_t mouse_epoch = 0;
bool context_ready = false;
std::atomic<bool> running{false};
std::thread receiver;
std::filesystem::path config_path;

Configuration load_configuration(bool &valid) {
    valid = false;
    Configuration value;
    std::ifstream input(config_path, std::ios::binary);
    if (!input) return value;
    std::string text; char buffer[1024];
    while (input.read(buffer, sizeof buffer) || input.gcount()) {
        text.append(buffer, (size_t)input.gcount()); if (text.size() > 8192) return value;
    }
    if (!parse_settings(text, value)) return {};
    valid = true;
    return value;
}
void mouse_input(const MouseUpdate &change) {
    auto delivered = mouse_delivery.plan(change, desktop_mouse.pressed());
    INPUT event{}; event.type = INPUT_MOUSE;
    event.mi.dx = delivered.dx; event.mi.dy = delivered.dy;
    event.mi.dwFlags = (delivered.dx || delivered.dy ? MOUSEEVENTF_MOVE : 0)
        | (delivered.down ? MOUSEEVENTF_LEFTDOWN : 0) | (delivered.up ? MOUSEEVENTF_LEFTUP : 0);
    SetLastError(ERROR_SUCCESS);
    bool success = !event.mi.dwFlags || SendInput(1, &event, sizeof event) == 1;
    DWORD error = success ? 0 : GetLastError();
    mouse_delivery.complete(delivered, success, success ? 0 : (int)(error ? error : ERROR_GEN_FAILURE));
}
int delivery_error(const Controls &hand) {
    return hand.delivery.error() ? hand.delivery.error() : hand.scroll_delivery.error();
}
void publish(Controls &hand, const Output &value, const DashboardScroll &scroll = {}) {
    if (!hand.container) return;
    auto *input = VRDriverInput();
    std::array<int, 5> errors{
        input->UpdateScalarComponent(hand.handles[0], value.x, 0),
        input->UpdateScalarComponent(hand.handles[1], value.y, 0),
        input->UpdateScalarComponent(hand.handles[2], value.force, 0),
        input->UpdateBooleanComponent(hand.handles[3], value.touch, 0),
        input->UpdateBooleanComponent(hand.handles[4], value.click, 0)};
    hand.delivery.complete(value, errors);
    std::array<int, 3> scroll_errors{
        input->UpdateScalarComponent(hand.scroll_handles[0], scroll.x, 0),
        input->UpdateScalarComponent(hand.scroll_handles[1], scroll.y, 0),
        input->UpdateBooleanComponent(hand.scroll_handles[2], scroll.touch, 0)};
    hand.scroll_delivery.complete(scroll, scroll_errors);
    if (!hand.scroll_motion_logged && (scroll.x != 0 || scroll.y != 0)) {
        hand.scroll_motion_logged = true;
        log("SWIPE_PUBLICATION device=" + std::to_string(hand.device)
            + " side=" + std::string(VRProperties()->GetInt32Property(hand.container, Prop_ControllerRoleHint_Int32) == TrackedControllerRole_RightHand ? "right" : "left")
            + " mode=swipe"
            + " container=" + std::to_string(hand.container)
            + " driverContext=" + std::to_string(VRDriverContext()->GetDriverHandle())
            + " parentDriver=" + std::to_string(VRProperties()->GetUint64Property(hand.container, Prop_ParentDriver_Uint64))
            + " handles=" + std::to_string(hand.scroll_handles[0]) + "," + std::to_string(hand.scroll_handles[1])
            + "," + std::to_string(hand.scroll_handles[2])
            + " vector=" + std::to_string(scroll.x) + "," + std::to_string(scroll.y)
            + " touch=" + (scroll.touch ? "true" : "false")
            + " errors=" + std::to_string(scroll_errors[0]) + "," + std::to_string(scroll_errors[1])
            + "," + std::to_string(scroll_errors[2]));
    }
}
void release_hand(Controls &hand, int side) {
    mouse_input(desktop_mouse.release(side)); publish(hand, {});
    if (hand.container) {
        if (delivery_error(hand)) { ++restore_errors; log("controller neutral update rejected during release"); }
        if (VRProperties()->GetStringProperty(hand.container, Prop_InputProfilePath_String) == profile
            && (VRProperties()->SetStringProperty(hand.container, Prop_InputProfilePath_String, hand.original_profile.c_str()) != TrackedProp_Success
                || VRProperties()->GetStringProperty(hand.container, Prop_InputProfilePath_String) != hand.original_profile)) {
            ++restore_errors; log("controller input profile restoration did not verify");
        }
    }
    hand = {};
    input_creation_errors[side] = 0;
}
void deactivate(PropertyContainerHandle_t container, uint32_t id) {
    std::lock_guard<std::mutex> guard(state_mutex);
    availability.deactivated(id, container);
    if (!context_ready || !container) return;
    for (int side = 0; side < 2; ++side) if (hands[side].container == container) release_hand(hands[side], side);
}
PropertyContainerHandle_t remember_container(TrackedDeviceIndex_t id) {
    std::lock_guard<std::mutex> guard(state_mutex);
    return context_ready ? VRProperties()->TrackedDeviceToPropertyContainer(id) : 0;
}
void adopt(TrackedDeviceIndex_t id) {
    std::lock_guard<std::mutex> guard(state_mutex);
    // Registration follows the opted-in installed add-on's device lifetime.
    // Enabled/fresh gating below controls values, not the input schema.
    if (!context_ready) return;
    auto *properties = VRProperties(); auto container = properties->TrackedDeviceToPropertyContainer(id);
    // Deactivate can leave readable properties behind. Only a fresh Activate
    // or a different container can re-adopt that wrapped driver.
    if (!availability.can_adopt(id, container)) return;
    if (properties->GetInt32Property(container, Prop_DeviceClass_Int32) != TrackedDeviceClass_Controller) return;
    auto model = properties->GetStringProperty(container, Prop_RenderModelName_String);
    auto native_profile = properties->GetStringProperty(container, Prop_InputProfilePath_String);
    auto role = properties->GetInt32Property(container, Prop_ControllerRoleHint_Int32);
    if (model.find("quest_pro") == std::string::npos
        || (role != TrackedControllerRole_LeftHand && role != TrackedControllerRole_RightHand)) return;
    int side = role == TrackedControllerRole_RightHand;
    bool owned = hands[side].container == container;
    if (native_profile != "{oculus}/input/touch_profile.json" && !(owned && native_profile == profile)) return;
    discovery.remember(id);
    if (hands[side].container == container) return;
    // Existing controllers can have been activated before our provider loaded.
    // A new container for the same side must not inherit old component handles.
    if (hands[side].container) release_hand(hands[side], side);
    Controls pending; pending.container = container; pending.device = id; pending.original_profile = native_profile;
    // Set and verify the unchanged Touch identity's additive profile before
    // SteamVR creates handles for inputs absent from the original profile.
    auto restore_original_profile = [&]() {
        properties->SetStringProperty(container, Prop_InputProfilePath_String, native_profile.c_str());
        // A rejected write may already leave the original value in place.
        // Report recovery from verified state rather than the setter alone.
        ETrackedPropertyError read_error = TrackedProp_Success;
        const auto restored_profile = properties->GetStringProperty(container, Prop_InputProfilePath_String, &read_error);
        const bool restored = read_error == TrackedProp_Success && restored_profile == native_profile;
        if (!restored) { ++restore_errors; log("profile setup rollback did not verify; restart SteamVR before retrying"); }
        return restored;
    };
    const auto profile_error = properties->SetStringProperty(container, Prop_InputProfilePath_String, profile);
    if (profile_error != TrackedProp_Success
        || properties->GetStringProperty(container, Prop_InputProfilePath_String) != profile) {
        input_creation_errors[side] = VRInputError_InvalidDevice;
        restore_original_profile();
        log("profile change did not verify; no new input components created"); return;
    }
    auto *input = VRDriverInput(); int creation_error = 0;
    auto created = [&](EVRInputError error) {
        if (error != VRInputError_None && !creation_error) creation_error = error;
    };
    auto scalar = [&](int index, const char *path, EVRScalarUnits units) {
        created(input->CreateScalarComponent(container, path, &pending.handles[index], VRScalarType_Absolute, units));
    };
    scalar(0, "/input/trackpad/x", VRScalarUnits_NormalizedTwoSided);
    scalar(1, "/input/trackpad/y", VRScalarUnits_NormalizedTwoSided);
    scalar(2, "/input/trackpad/force", VRScalarUnits_NormalizedOneSided);
    created(input->CreateBooleanComponent(container, "/input/trackpad/touch", &pending.handles[3]));
    created(input->CreateBooleanComponent(container, "/input/trackpad/click", &pending.handles[4]));
    created(input->CreateScalarComponent(container, "/input/qpro_scroll/x", &pending.scroll_handles[0], VRScalarType_Absolute, VRScalarUnits_NormalizedTwoSided));
    created(input->CreateScalarComponent(container, "/input/qpro_scroll/y", &pending.scroll_handles[1], VRScalarType_Absolute, VRScalarUnits_NormalizedTwoSided));
    created(input->CreateBooleanComponent(container, "/input/qpro_scroll/touch", &pending.scroll_handles[2]));
    input_creation_errors[side] = creation_error;
    if (creation_error) {
        const bool restored = restore_original_profile();
        log("input creation rejected; original controller profile " + std::string(restored ? "restored" : "restore unconfirmed")
            + " (error " + std::to_string(creation_error) + ")"); return;
    }
    // Keep Prop_ControllerType_String unchanged, preserving the ordinary Touch binding identity.
    hands[side] = std::move(pending); publish(hands[side], {});
    log(std::string(side ? "right" : "left") + " Touch Pro inputs attached; application binding may require controller reconnect");
}

// Wrap the public server-driver ABI, forwarding every original operation. There
// are no Virtual Desktop private offsets and no changes to controller poses.
class ControllerAdapter final : public ITrackedDeviceServerDriver {
    ITrackedDeviceServerDriver *original_;
    PropertyContainerHandle_t container_ = 0;
    uint32_t id_ = k_unTrackedDeviceIndexInvalid;
public:
    explicit ControllerAdapter(ITrackedDeviceServerDriver *original) : original_(original) {}
    EVRInitError Activate(uint32_t id) override {
        auto error = original_->Activate(id);
        if (error == VRInitError_None) {
            {
                std::lock_guard<std::mutex> guard(state_mutex); discovery.activated(id);
                availability.activated(id);
            }
            id_ = id; container_ = remember_container(id); adopt(id);
        }
        return error;
    }
    void Deactivate() override {
        deactivate(container_, id_); container_ = 0; id_ = k_unTrackedDeviceIndexInvalid; original_->Deactivate();
    }
    void EnterStandby() override { original_->EnterStandby(); }
    void *GetComponent(const char *name) override { return original_->GetComponent(name); }
    void DebugRequest(const char *request, char *response, uint32_t length) override { original_->DebugRequest(request, response, length); }
    DriverPose_t GetPose() override { return original_->GetPose(); }
};
using DeviceAdded = bool (*)(IVRServerDriverHost *, const char *, ETrackedDeviceClass, ITrackedDeviceServerDriver *);
DeviceAdded previous_added = nullptr;
void **added_slot = nullptr;
std::mutex adapters_mutex;
std::vector<std::unique_ptr<ControllerAdapter>> adapters;
bool device_added(IVRServerDriverHost *host, const char *serial, ETrackedDeviceClass type, ITrackedDeviceServerDriver *device) {
    if (device && type == TrackedDeviceClass_Controller) {
        std::lock_guard<std::mutex> guard(adapters_mutex);
        adapters.push_back(std::make_unique<ControllerAdapter>(device)); device = adapters.back().get();
    }
    // Do not retain our adapter lock across the original activation callback.
    return previous_added(host, serial, type, device);
}
bool replace_slot(void *expected, void *replacement) {
    DWORD protection; if (!added_slot || !VirtualProtect(added_slot, sizeof(void *), PAGE_READWRITE, &protection)) return false;
    void *observed = InterlockedCompareExchangePointer((void *volatile *)added_slot, replacement, expected);
    DWORD discarded; VirtualProtect(added_slot, sizeof(void *), protection, &discarded);
    return observed == expected;
}
bool attach_host() {
    auto *host = (IVRServerDriverHost *)VRDriverContext()->GetGenericInterface(IVRServerDriverHost_Version);
    if (!host) return false;
    // TrackedDeviceAdded is the first method in this pinned, public OpenVR interface.
    added_slot = *reinterpret_cast<void ***>(host); previous_added = reinterpret_cast<DeviceAdded>(*added_slot);
    return replace_slot(reinterpret_cast<void *>(previous_added), reinterpret_cast<void *>(&device_added));
}
void receive_packets(SOCKET socket) {
    uint64_t next_settings = 0, next_health = 0;
    while (running) {
        if (clock_ns() >= next_settings) {
            bool valid; auto next = load_configuration(valid);
            { std::lock_guard<std::mutex> guard(state_mutex); latest_configuration.publish(next, valid); }
            next_settings = clock_ns() + 250000000;
        }
        fd_set readable; FD_ZERO(&readable); FD_SET(socket, &readable); timeval delay{0, 50000};
        if (select(0, &readable, nullptr, nullptr, &delay) <= 0) continue;
        uint8_t bytes[QPTP_PACKET_BYTES + 1]; sockaddr_in sender{}; int size = sizeof sender;
        int count = recvfrom(socket, (char *)bytes, sizeof bytes, 0, (sockaddr *)&sender, &size);
        if (sender.sin_addr.s_addr != htonl(INADDR_LOOPBACK) || count <= 0) continue;
        std::string nonce;
        if (decode_health_request(bytes, (size_t)count, nonce)) {
            if (clock_ns() < next_health) continue;
            next_health = clock_ns() + 250000000;
            DriverHealth health;
            {
                std::lock_guard<std::mutex> guard(state_mutex);
                health.enabled = configuration.enabled; health.settings_valid = settings_valid;
                health.mode = configuration.input.mode; health.side = configuration.input.side;
                health.settings_revision = latest_configuration.revision(); health.applied_revision = applied_configuration;
                health.mouse_error = mouse_delivery.error(); health.restore_errors = restore_errors;
                health.neutral = applied_configuration != 0 && !mouse_delivery.pressed();
                for (int side = 0; side < 2; ++side) {
                    const auto &hand = hands[side]; health.adopted[side] = hand.container != 0;
                    health.input_errors[side] = hand.container ? delivery_error(hand) : input_creation_errors[side];
                    health.successful[side] = hand.container != 0 && !health.input_errors[side];
                    health.neutral &= !hand.container || (hand.delivery.neutral() && hand.scroll_delivery.neutral());
                }
            }
            auto reply = encode_health(health, nonce);
            sendto(socket, reply.c_str(), (int)reply.size(), 0, (sockaddr *)&sender, size);
            continue;
        }
        QptpPacket value;
        if (count != QPTP_PACKET_BYTES
            || !qptp_decode(bytes, (size_t)count, &value)) continue;
        std::lock_guard<std::mutex> guard(state_mutex); lease.accept(value, clock_ns());
    }
    closesocket(socket);
}
class ControllerProvider final : public IServerTrackedDeviceProvider {
public:
    EVRInitError Init(IVRDriverContext *context) override {
        VR_INIT_SERVER_DRIVER_CONTEXT(context);
        HMODULE module = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&clock_ns), &module)) return VRInitError_Driver_Failed;
        wchar_t path[32768]; DWORD length = GetModuleFileNameW(module, path, 32768);
        if (!length || length >= 32768) return VRInitError_Driver_Failed;
        config_path = std::filesystem::path(path).parent_path().parent_path().parent_path() / "resources" / "settings.json";
        configuration = {}; applied_configuration = 0; settings_valid = false; discovery = {}; restore_errors = 0;
        input_creation_errors = {};
        availability = {};
        mouse_delivery = {}; desktop_mouse = {}; mouse_epoch = 0;
        latest_configuration = LatestConfiguration{};
        bool valid; auto initial = load_configuration(valid); latest_configuration.publish(initial, valid);
        WSADATA data; if (WSAStartup(MAKEWORD(2, 2), &data)) return VRInitError_Driver_Failed;
        SOCKET socket = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        sockaddr_in address{}; address.sin_family = AF_INET; address.sin_port = htons(27064); address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (socket == INVALID_SOCKET || bind(socket, (sockaddr *)&address, sizeof address) == SOCKET_ERROR) {
            if (socket != INVALID_SOCKET) closesocket(socket); WSACleanup(); log("UDP27064 unavailable; controller addon inactive"); return VRInitError_Driver_Failed;
        }
        u_long nonblocking = 1;
        if (ioctlsocket(socket, FIONBIO, &nonblocking) == SOCKET_ERROR) {
            closesocket(socket); WSACleanup(); log("nonblocking UDP unavailable"); return VRInitError_Driver_Failed;
        }
        if (!attach_host()) { closesocket(socket); WSACleanup(); log("public OpenVR host adoption unavailable"); return VRInitError_Driver_Failed; }
        { std::lock_guard<std::mutex> guard(state_mutex); context_ready = true; }
        running = true;
        try { receiver = std::thread(receive_packets, socket); }
        catch (...) {
            running = false;
            replace_slot(reinterpret_cast<void *>(&device_added), reinterpret_cast<void *>(previous_added));
            { std::lock_guard<std::mutex> guard(state_mutex); context_ready = false; }
            closesocket(socket); WSACleanup(); log("receiver thread unavailable; addon startup unwound");
            VR_CLEANUP_SERVER_DRIVER_CONTEXT(); return VRInitError_Driver_Failed;
        }
        log("experimental addon ready; default disabled, waiting for opted-in Qpro input"); return VRInitError_None;
    }
    void Cleanup() override {
        running = false; if (receiver.joinable()) receiver.join();
        if (!replace_slot(reinterpret_cast<void *>(&device_added), reinterpret_cast<void *>(previous_added)))
            log("public OpenVR host restoration was not confirmed; hot unloading remains unsupported");
        std::lock_guard<std::mutex> guard(state_mutex);
        if (!context_ready) return;
        context_ready = false;
        for (int side = 0; side < 2; ++side) release_hand(hands[side], side);
        if (mouse_delivery.pressed()) { ++restore_errors; log("desktop mouse release was not confirmed during shutdown"); }
        lease.clear(); WSACleanup(); VR_CLEANUP_SERVER_DRIVER_CONTEXT();
        // SteamVR may still hold forwarded driver pointers during shutdown. The
        // adapters live through process teardown; hot unloading is unsupported.
    }
    const char *const *GetInterfaceVersions() override { return k_InterfaceVersions; }
    void RunFrame() override {
        uint64_t now = clock_ns();
        std::array<bool, k_unMaxTrackedDeviceCount> selected{};
        {
            std::lock_guard<std::mutex> guard(state_mutex);
            Configuration next; bool valid;
            if (latest_configuration.take(applied_configuration, next, valid)) {
                if (next.input.mode != configuration.input.mode || next.input.side != configuration.input.side) {
                    // Release once before reanchoring the selected thumbs.
                    mouse_input(desktop_mouse.step({}, false, next.input));
                    for (auto &hand : hands) {
                        hand.filter.clear(); publish(hand, {});
                        hand.scroll_motion_logged = false;
                    }
                }
                configuration = next; settings_valid = valid;
            }
            selected = discovery.poll(true, now);
        }
        for (uint32_t id = 0; id < k_unMaxTrackedDeviceCount; ++id)
            if (selected[id]) adopt(id);
        std::lock_guard<std::mutex> guard(state_mutex); QptpPacket packet;
        // Receiver acceptance uses this same lock. Sampling after acquiring it
        // prevents a live arrival from being newer than this frame timestamp.
        now = clock_ns();
        uint64_t revision = 0, epoch = 0;
        bool fresh = configuration.enabled && lease.current(now, packet, &revision, &epoch);
        if (!fresh || mouse_epoch != epoch) {
            // A resumed reader must not move from the previous reader's mouse
            // anchor, even when no render frame observed the expired lease.
            mouse_input(desktop_mouse.step({}, false, configuration.input));
            mouse_epoch = fresh ? epoch : 0;
        }
        std::array<Output, 2> outputs{};
        for (int side = 0; side < 2; ++side) {
            auto &hand = hands[side];
            if (fresh) outputs[side] = hand.filter.step_packet(packet.sides[side], now, configuration.input,
                packet.monotonic_ns, revision, epoch, side, hand.container != 0);
            else hand.filter.clear();
        }
        bool mouse = fresh && configuration.input.mode == Mode::Mouse;
        mouse_input(desktop_mouse.step(outputs, mouse, configuration.input));
        for (int side = 0; side < 2; ++side) {
            const auto scroll = dashboard_scroll_output(configuration.input, configuration.enabled, fresh,
                hands[side].container != 0, side, fresh ? packet.sides[side] : QptpSide{}, outputs[side]);
            publish(hands[side], mouse ? Output{} : outputs[side], scroll);
        }
    }
    bool ShouldBlockStandbyMode() override { return false; }
    void EnterStandby() override {}
    void LeaveStandby() override {}
};
ControllerProvider provider;
}
extern "C" __declspec(dllexport) void *HmdDriverFactory(const char *name, int *error) {
    if (name && !strcmp(name, vr::IServerTrackedDeviceProvider_Version)) return &provider;
    if (error) *error = vr::VRInitError_Init_InterfaceNotFound; return nullptr;
}
