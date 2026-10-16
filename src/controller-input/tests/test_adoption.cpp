#include <array>
#include <iostream>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>
using PropertyContainerHandle_t = uint64_t;
using TrackedDeviceIndex_t = uint32_t;
using VRInputComponentHandle_t = uint64_t;
using EVRScalarUnits = int;
enum { TrackedDeviceClass_Controller = 2, TrackedControllerRole_LeftHand = 1, TrackedControllerRole_RightHand = 2,
    Prop_DeviceClass_Int32, Prop_ControllerRoleHint_Int32, Prop_RenderModelName_String, Prop_InputProfilePath_String,
    VRScalarType_Absolute, VRScalarUnits_NormalizedTwoSided, VRScalarUnits_NormalizedOneSided };
enum EVRInputError { VRInputError_None, VRInputError_InvalidDevice = 3, FakeCreationFailure = 77 };
enum ETrackedPropertyError { TrackedProp_Success = 0, TrackedProp_UnknownProperty = 4 };
constexpr char profile[] = "{qpro_controller}/input/quest_pro_touchpad.json";
constexpr char original[] = "{oculus}/input/touch_profile.json";
struct Controls {
    uint64_t container = 0; uint32_t device = 0;
    std::array<uint64_t, 5> handles{}; std::array<uint64_t, 3> scroll_handles{};
    std::string original_profile;
};
std::mutex state_mutex;
bool context_ready = true;
struct { bool enabled = true; } configuration;
struct { bool can_adopt(uint32_t, uint64_t) { return true; } } availability;
struct { void remember(uint32_t) {} } discovery;
std::array<Controls, 2> hands{};
std::array<int, 2> input_creation_errors{};
uint64_t restore_errors = 0;
std::vector<std::string> events;
int publications = 0;
void log(const std::string &) {}
void publish(Controls &, int) { ++publications; }
void release_hand(Controls &hand, int) { hand = {}; }
struct Properties {
    std::string current = original;
    bool reject_custom = false, reject_restore = false, mismatch_custom_read = false, fail_restore_read = false;
    bool last_write_was_restore = false;
    int role = TrackedControllerRole_LeftHand;
    uint64_t TrackedDeviceToPropertyContainer(uint32_t) { return 123; }
    int GetInt32Property(uint64_t, int prop) { return prop == Prop_DeviceClass_Int32 ? TrackedDeviceClass_Controller : role; }
    std::string GetStringProperty(uint64_t, int prop, ETrackedPropertyError *error = nullptr) {
        if (error) *error = TrackedProp_Success;
        if (prop == Prop_RenderModelName_String) return "oculus_quest_pro_controller_left";
        events.push_back("read:" + current);
        if (fail_restore_read && last_write_was_restore && error) {
            *error = TrackedProp_UnknownProperty; return "";
        }
        if (mismatch_custom_read && current == profile) { mismatch_custom_read = false; return "unexpected"; }
        return current;
    }
    int SetStringProperty(uint64_t, int, const char *value) {
        events.push_back("write:" + std::string(value));
        last_write_was_restore = value == std::string(original);
        if ((reject_custom && value == std::string(profile)) || (reject_restore && value == std::string(original))) return 1;
        current = value; return TrackedProp_Success;
    }
} properties;
Properties *VRProperties() { return &properties; }
struct Input {
    int creates = 0, fail_at = -1;
    bool all_created_under_custom_profile = true;
    EVRInputError create(uint64_t container, uint64_t *handle) {
        ++creates; events.push_back("create");
        all_created_under_custom_profile &= properties.current == profile && container == 123;
        *handle = uint64_t(100 + creates);
        return creates == fail_at ? FakeCreationFailure : VRInputError_None;
    }
    EVRInputError CreateScalarComponent(uint64_t container, const char *, uint64_t *handle, int, int) { return create(container, handle); }
    EVRInputError CreateBooleanComponent(uint64_t container, const char *, uint64_t *handle) { return create(container, handle); }
} input;
Input *VRDriverInput() { return &input; }
#include "adopt_under_test.inc"
int checks = 0;
void expect(bool value, const char *message) { ++checks; if (!value) throw std::runtime_error(message); }
void reset() {
    properties = {}; input = {}; hands = {}; input_creation_errors = {}; restore_errors = 0; publications = 0;
    context_ready = true; configuration.enabled = true; events.clear();
}
int main() {
    try {
    for (int role : {TrackedControllerRole_LeftHand, TrackedControllerRole_RightHand}) {
        reset(); properties.role = role; adopt(4);
        const int side = role == TrackedControllerRole_RightHand;
        expect(hands[side].container == 123 && hands[side].device == 4, "same device/container adopted");
        expect(properties.current == profile && input.creates == 8, "all additive components created");
        expect(input.all_created_under_custom_profile, "profile set before every Create call");
        expect(events.size() >= 4 && events[1] == std::string("write:") + profile
            && events[2] == std::string("read:") + profile && events[3] == "create", "custom profile readback precedes first Create");
        expect(hands[side].original_profile == original && publications == 1, "original profile retained and initial neutral publish");
        expect(input_creation_errors[side] == 0 && restore_errors == 0, "successful path stays healthy");
    }
    for (int failure = 1; failure <= 8; ++failure) {
        reset(); input.fail_at = failure; adopt(4);
        expect(!hands[0].container && !hands[1].container && publications == 0, "creation failure never adopts or publishes");
        expect(properties.current == original && restore_errors == 0, "creation failure restores original profile");
        expect(events[events.size() - 2] == std::string("write:") + original
            && events.back() == std::string("read:") + original, "rollback readback verified");
        expect(input_creation_errors[0] == FakeCreationFailure, "creation error retained");
        expect(input.all_created_under_custom_profile, "even failure creates only under custom profile");
    }
    reset(); properties.reject_custom = true; adopt(4);
    expect(input.creates == 0 && !hands[0].container, "profile set failure blocks component creation");
    expect(properties.current == original && input_creation_errors[0] == VRInputError_InvalidDevice, "profile set failure stays original");
    reset(); properties.mismatch_custom_read = true; adopt(4);
    expect(input.creates == 0 && !hands[0].container, "profile readback mismatch blocks creation");
    expect(properties.current == original && restore_errors == 0, "profile readback mismatch restores original");
    reset(); input.fail_at = 1; properties.reject_restore = true; adopt(4);
    expect(!hands[0].container && restore_errors == 1, "rollback failure is explicit and cannot claim adoption");
    expect(properties.current == profile && input_creation_errors[0] == FakeCreationFailure, "unconfirmed rollback retains diagnostic error");
    reset(); properties.reject_custom = true; properties.reject_restore = true; adopt(4);
    expect(properties.current == original && restore_errors == 0, "rejected restoration write is healthy when original state verifies");
    expect(events.back() == std::string("read:") + original, "restoration is read back even when its setter fails");
    reset(); input.fail_at = 1; properties.fail_restore_read = true; adopt(4);
    expect(properties.current == original && restore_errors == 1, "unreadable restoration state remains unconfirmed");
    reset(); configuration.enabled = false; adopt(4);
    expect(input.creates == 8 && hands[0].container == 123, "installed add-on registers schema while output disabled");
    expect(publications == 1, "disabled adoption publishes initial neutral values only");
    reset(); context_ready = false; adopt(4);
    expect(input.creates == 0 && events.empty(), "missing driver context cannot alter device");
    std::cout << checks << " exact-provider adoption checks passed\n";
    return 0;
    } catch (const std::exception &error) {
        std::cerr << "FAIL after " << checks << ": " << error.what() << '\n';
        return 1;
    }
}
