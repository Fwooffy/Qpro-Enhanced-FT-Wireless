'use strict';
// The host admits this adapter only for the exact driver fingerprint in the
// compatibility profile. Every live object and public interface is rechecked.
const configuration = globalThis.QPRO_PROFILE;
const layout = configuration.pcLayout;
let state = 'idle', error = null, driver = null, input = null, updateAddress = null;
let updateHooks = [], updateTargets = [], poseHook = null, observer = null, cleanupScheduled = false;
let poll = null, lease = null, interfaceVersion = null;
let native = null, cleanupPromise = null;
const sides = [null, null], submissions = [0, 0];
const submissionAge = [0xffffffff, 0xffffffff];
const activeSides = [false, false];
let restored = 0;
const calls = {seen: 0, inputOwner: 0, matched: 0, successful: 0, invalidBones: 0, poseCopies: 0,
    sources: {'vd-original': 0}};
const resultCounts = {};
const routes = [0, 1].map(() => ({reason: 'not-running', physical: false, optical: false, handle: false}));

// Frida's native listeners avoid entering JavaScript on SteamVR's driver
// threads. Only the slow route/lease poll and RPC snapshots cross that boundary.
// Writable storage is supplied explicitly: CModule's own data is read-only.
const nativeSource = `
#include <gum/guminterceptor.h>
#include <gum/gummemory.h>
#include <gum/gumspinlock.h>
#include <string.h>

typedef struct {
  double time_offset, world_rotation[4], world_translation[3];
  double head_rotation[4], head_translation[3], position[3], velocity[3];
  double acceleration[3], rotation[4], angular_velocity[3], angular_acceleration[3];
  gint32 result;
  guint8 valid, drift, head_model, connected;
} DriverPose;
typedef struct {
  gpointer controller, hand;
  guint64 handle;
  guint device, physical, active, generation;
} Side;
typedef struct {
  GumSpinlock lock;
  guint enabled, module_alive, fault, poses;
  gpointer table, input;
  gsize hand_offset;
  Side sides[2];
  guint seen, owner, matched, successful, invalid, copies, submissions[2];
  guint results[32], other_results;
  guint64 last_submission[2];
} State;
typedef struct { guint side, generation, valid; Side record; } SkeletonCall;
typedef struct { DriverPose * copy; } PoseCall;
extern State qpro_state;
/* Windows uptime is monotonic and uses the same clock for native timestamps
 * and snapshot ages; it is never compared with JavaScript's wall clock. */
extern guint64 qpro_now(void);

/* Never dereference a cached driver object. A module/object may disappear
 * between the JavaScript poll and this callback; partial reads are refused. */
static gboolean read_exact(gconstpointer address, gpointer output, gsize size) {
  gsize count = 0;
  guint8 * bytes;
  if (address == NULL) return FALSE;
  bytes = gum_memory_read(address, size, &count);
  if (bytes == NULL) return FALSE;
  if (count == size) memcpy(output, bytes, size);
  g_free(bytes);
  return count == size;
}
static gboolean identity(const Side * side, guint index) {
  gpointer controller = NULL, hand = NULL;
  return read_exact((guint8 *) qpro_state.table + index * sizeof(gpointer), &controller, sizeof(controller)) &&
    controller == side->controller && controller != NULL &&
    read_exact((guint8 *) controller + qpro_state.hand_offset, &hand, sizeof(hand)) && hand == side->hand;
}
static void fault(guint code) {
  gum_spinlock_acquire(&qpro_state.lock);
  if (qpro_state.fault == 0) qpro_state.fault = code;
  qpro_state.enabled = FALSE;
  gum_spinlock_release(&qpro_state.lock);
}
static gboolean current(const Side * side, guint index) {
  const Side * now = &qpro_state.sides[index];
  return qpro_state.enabled && qpro_state.module_alive && !qpro_state.fault &&
    now->generation == side->generation && now->controller == side->controller && now->hand == side->hand;
}
guint qpro_state_size(void) { return sizeof(State); }
void qpro_abi(guint * output) {
  output[0] = sizeof(DriverPose); output[1] = offsetof(DriverPose, valid);
  output[2] = offsetof(DriverPose, connected); output[3] = sizeof(SkeletonCall);
  output[4] = sizeof(PoseCall); output[5] = sizeof(gpointer);
}
void qpro_initialize(gpointer table, gpointer input, gsize hand_offset) {
  memset(&qpro_state, 0, sizeof(State));
  gum_spinlock_init(&qpro_state.lock);
  qpro_state.table = table; qpro_state.input = input; qpro_state.hand_offset = hand_offset;
  qpro_state.module_alive = TRUE;
}
void qpro_set_side(guint index, gpointer controller, gpointer hand, guint64 handle,
    guint device, guint physical, guint active) {
  Side * side;
  if (index >= 2) return;
  gum_spinlock_acquire(&qpro_state.lock);
  side = &qpro_state.sides[index];
  if (side->controller != controller || side->hand != hand || side->handle != handle ||
      side->device != device || side->physical != physical || side->active != active) side->generation++;
  side->controller = controller; side->hand = hand; side->handle = handle;
  side->device = device; side->physical = physical; side->active = active;
  gum_spinlock_release(&qpro_state.lock);
}
void qpro_enable(guint enabled) {
  gum_spinlock_acquire(&qpro_state.lock);
  qpro_state.enabled = enabled && qpro_state.module_alive && !qpro_state.fault;
  gum_spinlock_release(&qpro_state.lock);
}
void qpro_module_alive(guint alive) {
  gum_spinlock_acquire(&qpro_state.lock);
  qpro_state.module_alive = alive;
  if (!alive) qpro_state.enabled = FALSE;
  gum_spinlock_release(&qpro_state.lock);
}
/* Fixed-width snapshot avoids exposing C struct packing to JavaScript. */
void qpro_snapshot(guint * output) {
  guint i;
  guint64 now = qpro_now(), age;
  gum_spinlock_acquire(&qpro_state.lock);
  output[0] = qpro_state.seen; output[1] = qpro_state.owner; output[2] = qpro_state.matched;
  output[3] = qpro_state.successful; output[4] = qpro_state.invalid; output[5] = qpro_state.copies;
  output[6] = qpro_state.submissions[0]; output[7] = qpro_state.submissions[1];
  output[8] = qpro_state.fault; output[9] = qpro_state.poses;
  for (i = 0; i < 32; i++) output[10 + i] = qpro_state.results[i];
  output[42] = qpro_state.other_results;
  for (i = 0; i < 2; i++) {
    age = qpro_state.last_submission[i] == 0 ? 0xffffffffU : now - qpro_state.last_submission[i];
    output[43 + i] = age > 0xffffffffU ? 0xffffffffU : (guint) age;
  }
  gum_spinlock_release(&qpro_state.lock);
}
void qpro_skeleton_enter(GumInvocationContext * ic) {
  SkeletonCall * call = GUM_IC_GET_INVOCATION_DATA(ic, SkeletonCall);
  guint i, count;
  guint64 handle;
  guint32 values[248];
  gpointer owner, bones;
  Side side;
  if (call == NULL) { fault(1); return; }
  memset(call, 0, sizeof(*call)); call->side = 2;
  handle = (guint64) (guintptr) gum_invocation_context_get_nth_argument(ic, 1);
  owner = gum_invocation_context_get_nth_argument(ic, 0);
  gum_spinlock_acquire(&qpro_state.lock);
  if (!qpro_state.enabled || !qpro_state.module_alive || qpro_state.fault) {
    gum_spinlock_release(&qpro_state.lock); return;
  }
  qpro_state.seen++;
  if (owner == qpro_state.input) qpro_state.owner++;
  for (i = 0; i < 2; i++) if (qpro_state.sides[i].controller != NULL && qpro_state.sides[i].handle == handle) break;
  if (i == 2) { gum_spinlock_release(&qpro_state.lock); return; }
  side = qpro_state.sides[i];
  gum_spinlock_release(&qpro_state.lock);
  if (!identity(&side, i)) return;
  count = GPOINTER_TO_UINT(gum_invocation_context_get_nth_argument(ic, 4));
  bones = gum_invocation_context_get_nth_argument(ic, 3);
  call->valid = count == 31 && read_exact(bones, values, sizeof(values));
  /* IEEE-754 exponent 255 covers both infinities and every NaN, including
   * values TinyCC might otherwise optimize under floating point assumptions. */
  if (call->valid) for (count = 0; count < 248; count++) if ((values[count] & 0x7f800000U) == 0x7f800000U) {
    call->valid = FALSE; break;
  }
  gum_spinlock_acquire(&qpro_state.lock);
  if (current(&side, i)) {
    qpro_state.matched++;
    if (!call->valid) qpro_state.invalid++;
    call->side = i; call->generation = side.generation; call->record = side;
  }
  gum_spinlock_release(&qpro_state.lock);
}
void qpro_skeleton_leave(GumInvocationContext * ic) {
  SkeletonCall * call = GUM_IC_GET_INVOCATION_DATA(ic, SkeletonCall);
  gint result = GPOINTER_TO_INT(gum_invocation_context_get_return_value(ic));
  gboolean unchanged = call != NULL && call->side < 2 && identity(&call->record, call->side);
  guint64 now = qpro_now();
  gum_spinlock_acquire(&qpro_state.lock);
  if (qpro_state.enabled && qpro_state.module_alive && !qpro_state.fault) {
    if (result >= 0 && result < 32) qpro_state.results[result]++; else qpro_state.other_results++;
    if (result == 0) qpro_state.successful++;
    if (unchanged && result == 0 && call->valid && call->record.active &&
        current(&call->record, call->side) && qpro_state.sides[call->side].active) {
      qpro_state.submissions[call->side]++; qpro_state.last_submission[call->side] = now;
    }
  }
  gum_spinlock_release(&qpro_state.lock);
}
void qpro_pose_enter(GumInvocationContext * ic) {
  PoseCall * call = GUM_IC_GET_INVOCATION_DATA(ic, PoseCall);
  guint i, device = GPOINTER_TO_UINT(gum_invocation_context_get_nth_argument(ic, 1));
  guint size;
  Side side;
  DriverPose * copy;
  gpointer pose;
  gsize count = 0;
  if (call == NULL) { fault(1); return; }
  call->copy = NULL;
  gum_spinlock_acquire(&qpro_state.lock);
  if (!qpro_state.enabled || !qpro_state.module_alive || qpro_state.fault) {
    gum_spinlock_release(&qpro_state.lock); return;
  }
  for (i = 0; i < 2; i++) if (qpro_state.sides[i].controller != NULL && qpro_state.sides[i].device == device) break;
  if (i == 2) { gum_spinlock_release(&qpro_state.lock); return; }
  side = qpro_state.sides[i];
  gum_spinlock_release(&qpro_state.lock);
  if (!side.physical || !side.active || !identity(&side, i)) return;
  size = GPOINTER_TO_UINT(gum_invocation_context_get_nth_argument(ic, 3));
  pose = gum_invocation_context_get_nth_argument(ic, 2);
  if (size != sizeof(DriverPose)) { fault(2); return; }
  copy = (DriverPose *) gum_memory_read(pose, sizeof(DriverPose), &count);
  if (copy == NULL || count != sizeof(DriverPose)) { g_free(copy); fault(2); return; }
  copy->valid = FALSE; copy->connected = FALSE;
  gum_spinlock_acquire(&qpro_state.lock);
  if (!current(&side, i) || !qpro_state.sides[i].physical || !qpro_state.sides[i].active) {
    gum_spinlock_release(&qpro_state.lock); g_free(copy); return;
  }
  /* Disable and accepting a redirected call use the same lock. Cleanup waits
   * for these paired calls while the leave listener is still attached. */
  qpro_state.poses++; qpro_state.copies++; call->copy = copy;
  gum_spinlock_release(&qpro_state.lock);
  gum_invocation_context_replace_nth_argument(ic, 2, copy);
}
void qpro_pose_leave(GumInvocationContext * ic) {
  PoseCall * call = GUM_IC_GET_INVOCATION_DATA(ic, PoseCall);
  DriverPose * copy;
  if (call == NULL || call->copy == NULL) return;
  /* A separate heap allocation is stable across nested native calls. Gum's
   * invocation-data array itself may move when its call stack grows. */
  copy = call->copy; call->copy = NULL; g_free(copy);
  gum_spinlock_acquire(&qpro_state.lock);
  qpro_state.poses--;
  gum_spinlock_release(&qpro_state.lock);
}
`;

function prepareNative() {
    if (native !== null) return;
    if (typeof CModule !== 'function' || Process.arch !== 'x64' || Process.pointerSize !== 8 || configuration.boneCount !== 31)
        throw new Error('Native SteamVR listeners require the admitted 64-bit Frida runtime and 31-bone ABI');
    const storage = Memory.alloc(4096), abi = Memory.alloc(24), snapshot = Memory.alloc(45 * 4);
    let module = null;
    try {
        const clock = Process.getModuleByName('kernel32.dll').getExportByName('GetTickCount64');
        if (!executable(clock)) throw new Error('The native monotonic clock is unavailable');
        module = new CModule(nativeSource, {qpro_state: storage, qpro_now: clock}, {toolchain: 'internal'});
        const fn = (name, result, args) => new NativeFunction(module[name], result, args,
            {scheduling: 'cooperative', traps: 'none'});
        const size = fn('qpro_state_size', 'uint', [])();
        fn('qpro_abi', 'void', ['pointer'])(abi);
        const sizes = Array.from({length: 6}, (_, index) => abi.add(index * 4).readU32());
        if (size > 4096 || size === 0 || sizes[0] !== 280 || sizes[1] !== 276 || sizes[2] !== 279 ||
            sizes[3] === 0 || sizes[3] > 1024 || sizes[4] === 0 || sizes[4] > 1024 || sizes[5] !== 8)
            throw new Error('Native SteamVR listener ABI validation failed');
        const prepared = {module, storage, snapshot, attached: false,
            initialize: fn('qpro_initialize', 'void', ['pointer', 'pointer', 'size_t']),
            setSide: fn('qpro_set_side', 'void', ['uint', 'pointer', 'pointer', 'uint64', 'uint', 'uint', 'uint']),
            enable: fn('qpro_enable', 'void', ['uint']), moduleAlive: fn('qpro_module_alive', 'void', ['uint']),
            readSnapshot: fn('qpro_snapshot', 'void', ['pointer'])};
        prepared.initialize(driver.base.add(layout.controllerTable), input, layout.handPointer);
        native = prepared;
    } catch (failure) {
        if (module !== null) module.dispose(); // No listener was attached yet.
        throw new Error('Native SteamVR listeners could not be prepared: ' + failure);
    }
}
function syncSide(side) {
    const record = sides[side];
    native.setSide(side, record?.controller || ptr(0), record?.hand || ptr(0), record?.handle || uint64(0),
        record?.handDevice || 0, record?.physical ? 1 : 0, record?.routeActive ? 1 : 0);
}
function refreshNative() {
    if (native === null) return {fault: 0, poses: 0};
    native.readSnapshot(native.snapshot);
    const values = Array.from({length: 45}, (_, index) => native.snapshot.add(index * 4).readU32());
    [calls.seen, calls.inputOwner, calls.matched, calls.successful, calls.invalidBones, calls.poseCopies] = values;
    calls.sources['vd-original'] = calls.seen;
    for (let side = 0; side < 2; side++) {
        submissions[side] = values[6 + side];
        submissionAge[side] = values[43 + side];
    }
    for (let index = 0; index < 32; index++) if (values[10 + index]) resultCounts[index] = values[10 + index];
    if (values[42]) resultCounts.other = values[42];
    return {fault: values[8], poses: values[9]};
}

function readable(address, bytes) {
    if (address.isNull()) return false;
    const range = Process.findRangeByAddress(address);
    return range !== null && range.protection.includes('r') && address.add(bytes).compare(range.base.add(range.size)) <= 0;
}
function writable(address, bytes) {
    const range = Process.findRangeByAddress(address);
    return readable(address, bytes) && range.protection.includes('w');
}
function executable(address) {
    const range = Process.findRangeByAddress(address);
    return range !== null && range.protection.includes('x');
}
function requireObject(address, bytes, description) {
    if (!readable(address, bytes)) throw new Error(description + ' is not readable');
    return address;
}
function sameObjects(record) {
    if (driver === null || Process.findModuleByName('driver_VirtualDesktop.dll')?.base.toString() !== driver.base.toString()) return false;
    const controller = driver.base.add(layout.controllerTable + record.side * Process.pointerSize).readPointer();
    return controller.equals(record.controller) && readable(controller, layout.handPointer + Process.pointerSize) &&
        controller.add(layout.handPointer).readPointer().equals(record.hand);
}
function skeletonHandle(controller, side) {
    const saved = driver.base.add(layout.skeletonTable + side * 8).readU64();
    return saved.equals(0) ? controller.add(layout.skeleton).readU64() : saved;
}
function resolveSide(side) {
    const controller = driver.base.add(layout.controllerTable + side * Process.pointerSize).readPointer();
    if (controller.isNull()) return null;
    requireObject(controller, layout.skeleton + 8, 'Controller');
    const hand = controller.add(layout.handPointer).readPointer();
    if (hand.isNull()) return null;
    requireObject(hand, layout.skeleton + 8, 'Hand');
    if (controller.add(layout.role).readU32() !== side + 1 || hand.add(layout.role).readU32() !== side + 1)
        throw new Error('The hand/controller role does not match the compatibility profile');
    for (const object of [controller, hand]) {
        if (!writable(object.add(layout.multiModal), 1) || !writable(object.add(layout.skeleton), 8))
            throw new Error('Hand routing fields are not writable');
        if (object.add(layout.multiModal).readU8() > 1) throw new Error('The multimodal field has an unexpected value');
    }
    const device = controller.add(layout.deviceIndex).readU32();
    const handDevice = hand.add(layout.deviceIndex).readU32();
    if (device >= 64 || handDevice >= 64 || device === handDevice) throw new Error('The SteamVR device identities are invalid');
    return {side, controller, hand, device, handDevice, routeActive: false,
        original: {controllerMode: controller.add(layout.multiModal).readU8(),
            handMode: hand.add(layout.multiModal).readU8(), handle: hand.add(layout.skeleton).readU64()},
        handle: skeletonHandle(controller, side), physical: false};
}
function restore(record) {
    if (record === null || !sameObjects(record)) return;
    record.hand.add(layout.skeleton).writeU64(record.original.handle);
    record.hand.add(layout.multiModal).writeU8(record.original.handMode);
    record.controller.add(layout.multiModal).writeU8(record.original.controllerMode);
    if (!record.hand.add(layout.skeleton).readU64().equals(record.original.handle) ||
        record.hand.add(layout.multiModal).readU8() !== record.original.handMode ||
        record.controller.add(layout.multiModal).readU8() !== record.original.controllerMode)
        throw new Error('SteamVR hand routing restoration did not verify');
    restored++; record.routeActive = false;
}
function deactivate() {
    if (state === 'stopped') return Promise.resolve();
    if (cleanupPromise !== null) return cleanupPromise;
    state = 'restoring';
    const failures = [];
    if (poll !== null) { clearInterval(poll); poll = null; }
    if (lease !== null) { clearTimeout(lease); lease = null; }
    if (native !== null) native.enable(0);
    activeSides.fill(false);
    let complete;
    cleanupPromise = new Promise(resolve => { complete = resolve; });
    const deadline = Date.now() + 2000;
    function drain() {
        try {
            // Leave listeners remain attached until redirected original calls
            // return and free their private poses. Detach can skip onLeave.
            if (refreshNative().poses !== 0) {
                if (Date.now() < deadline) { setTimeout(drain, 10); return; }
                error = 'Native pose calls did not finish; disabled listeners and native memory are retained.';
                state = 'restore-failed'; complete(); return;
            }
            const remainingHooks = [];
            for (const hook of updateHooks) {
                try { hook.detach(); } catch (failure) { failures.push(String(failure)); remainingHooks.push(hook); }
            }
            updateHooks = remainingHooks;
            if (poseHook !== null) {
                try { poseHook.detach(); poseHook = null; } catch (failure) { failures.push(String(failure)); }
            }
            Interceptor.flush();
            if (observer !== null) {
                try { observer.detach(); observer = null; } catch (failure) { failures.push(String(failure)); }
            }
            for (const record of sides) {
                try { restore(record); } catch (failure) { failures.push(String(failure)); }
            }
            // flush() commits detach but does not prove native quiescence.
            // Retain the CModule, state and functions until Frida script unload,
            // whose native interceptor drain precedes CModule destruction.
            if (failures.length) { error = failures.join('; '); state = 'restore-failed'; }
            else state = 'stopped';
        } catch (failure) { error = String(failure); state = 'restore-failed'; }
        complete();
    }
    drain();
    return cleanupPromise;
}
function fail(failure) {
    error = String(failure);
    if (cleanupScheduled || state === 'stopped' || state === 'restore-failed') return;
    // Native callbacks must return before their listeners are detached or
    // shared driver fields are restored. Stop new routing writes immediately.
    state = 'failing'; cleanupScheduled = true;
    if (native !== null) native.enable(0);
    setImmediate(() => { cleanupScheduled = false; deactivate(); });
}
function heartbeat(seconds) {
    if (!Number.isFinite(seconds) || seconds < 1 || seconds > 30 || state !== 'running') throw new Error('Invalid skeleton lease');
    if (lease !== null) clearTimeout(lease);
    lease = setTimeout(deactivate, seconds * 1000);
}
function resolveInterfaces() {
    driver = Process.getModuleByName('driver_VirtualDesktop.dll');
    if (!Number.isSafeInteger(layout.skeletonOriginal) || layout.skeletonOriginal < 0)
        throw new Error('The saved skeleton callable is missing from this profile');
    if (driver.size <= Math.max(layout.skeletonTable, layout.controllerTable, layout.skeletonOriginal) + 16)
        throw new Error('The VD driver is too small for this profile');
    input = null; updateAddress = null; interfaceVersion = null; updateTargets = [];
    const context = requireObject(driver.base.add(layout.driverContext).readPointer(), Process.pointerSize, 'OpenVR driver context');
    const contextTable = requireObject(context.readPointer(), Process.pointerSize, 'OpenVR context table');
    const getAddress = contextTable.readPointer();
    if (!executable(getAddress)) throw new Error('OpenVR interface lookup is not executable');
    const getInterface = new NativeFunction(getAddress, 'pointer', ['pointer', 'pointer', 'pointer']);
    for (const name of configuration.interfaceVersions) {
        const result = Memory.alloc(4); result.writeS32(-1);
        const candidate = getInterface(context, Memory.allocUtf8String(name), result);
        if (candidate.isNull()) continue;
        requireObject(candidate, Process.pointerSize, 'OpenVR input interface');
        const table = requireObject(candidate.readPointer(), 7 * Process.pointerSize, 'OpenVR input vtable');
        const address = table.add(6 * Process.pointerSize).readPointer();
        if (!executable(address)) throw new Error('Skeleton update interface is invalid');
        input = candidate; updateAddress = address; interfaceVersion = name; break;
    }
    if (input === null) throw new Error('A supported OpenVR skeletal input interface is unavailable');
    const savedSlot = requireObject(driver.base.add(layout.skeletonOriginal), Process.pointerSize, 'Saved skeleton callable slot');
    const savedUpdate = savedSlot.readPointer();
    if (savedUpdate.isNull() || !executable(savedUpdate)) throw new Error('The saved skeleton callable is not executable');
    // VD sends optical bones through its saved original callable. Observe only
    // that callable: the public entry can return success for suppressed updates
    // and adds native-thread callbacks without establishing skeleton readiness.
    updateTargets = [{address: savedUpdate, source: 'vd-original'}];
    const host = requireObject(driver.base.add(layout.driverHost).readPointer(), Process.pointerSize, 'OpenVR driver host');
    const hostTable = requireObject(host.readPointer(), 2 * Process.pointerSize, 'OpenVR host vtable');
    const poseAddress = hostTable.add(Process.pointerSize).readPointer();
    if (!executable(poseAddress)) throw new Error('TrackedDevicePoseUpdated is invalid');
    return poseAddress;
}
function updateRoutes() {
    if (state !== 'running') return;
    try {
        if (Process.findModuleByName('driver_VirtualDesktop.dll')?.base.toString() !== driver.base.toString()) {
            native.moduleAlive(0); throw new Error('VD driver identity changed');
        }
        const snapshot = refreshNative();
        if (snapshot.fault) throw new Error(snapshot.fault === 2 ? 'DriverPose_t ABI mismatch or incomplete native read' :
            'Native listener invocation storage is unavailable');
        for (let side = 0; side < 2; side++) {
            let record = sides[side];
            if (record === null || !sameObjects(record)) record = sides[side] = resolveSide(side);
            if (record === null) {
                routes[side] = {reason: 'controller-unavailable', physical: false, optical: false, handle: false};
                activeSides[side] = false; syncSide(side); continue;
            }
            const data = record.controller.add(layout.dataPointer).readPointer();
            if (data.isNull()) {
                routes[side] = {reason: 'controller-data-unavailable', physical: false, optical: false, handle: false};
                restore(record); record.physical = false; activeSides[side] = false; syncSide(side); continue;
            }
            requireObject(data, layout.opticalFlags + 2, 'VD controller frame');
            const frame = data.readPointer();
            if (frame.isNull()) {
                routes[side] = {reason: 'tracking-frame-unavailable', physical: false, optical: false, handle: false};
                restore(record); record.physical = false; activeSides[side] = false; syncSide(side); continue;
            }
            requireObject(frame, layout.frameFlags + layout.frameSideStride * side + 1, 'VD tracking frame');
            const physical = (frame.add(layout.frameFlags + layout.frameSideStride * side).readU8() & 3) === 1;
            const optical = data.add(layout.opticalFlags + side).readU8();
            if (optical > 1) throw new Error('VD optical validity field is unsupported');
            const handle = skeletonHandle(record.controller, side);
            const enabled = physical && optical === 1 && !handle.equals(0);
            record.hand.add(layout.skeleton).writeU64(enabled ? handle : record.original.handle);
            record.hand.add(layout.multiModal).writeU8(physical ? 1 : record.original.handMode);
            record.controller.add(layout.multiModal).writeU8(enabled ? 1 : record.original.controllerMode);
            record.handle = handle; record.routeActive = enabled; record.physical = physical;
            syncSide(side);
            activeSides[side] = enabled && submissionAge[side] < 500;
            routes[side] = {reason: !physical ? 'controller-not-held' : optical !== 1 ? 'optical-hand-unavailable' :
                handle.equals(0) ? 'skeleton-handle-unavailable' : activeSides[side] ? 'active' : 'waiting-for-skeleton',
                physical, optical: optical === 1, handle: !handle.equals(0)};
        }
    } catch (failure) { fail(failure); }
}
rpc.exports = {
    validate() {
        resolveInterfaces(); sides[0] = resolveSide(0); sides[1] = resolveSide(1);
        prepareNative();
        return {compatible: true, interfaceVersion, boneCount: configuration.boneCount,
            controllersPresent: sides.map(record => record !== null), skeletonObservers: updateTargets.length,
            callbackBackend: 'native'};
    },
    activate(seconds) {
        if (state !== 'idle') throw new Error('Skeleton adapter is already used');
        const poseAddress = resolveInterfaces();
        sides[0] = resolveSide(0); sides[1] = resolveSide(1);
        prepareNative(); state = 'running';
        try {
            native.initialize(driver.base.add(layout.controllerTable), input, layout.handPointer);
            // Observe the saved public API implementation without replacing it;
            // every driver's input arguments and result pass through unchanged.
            native.attached = true;
            for (const target of updateTargets) updateHooks.push(Interceptor.attach(target.address, {
                onEnter: native.module.qpro_skeleton_enter, onLeave: native.module.qpro_skeleton_leave}));
            poseHook = Interceptor.attach(poseAddress, {
                onEnter: native.module.qpro_pose_enter, onLeave: native.module.qpro_pose_leave});
            observer = Process.attachModuleObserver({onRemoved(module) {
                if (module.base.equals(driver.base)) { native.moduleAlive(0); fail('VD driver unloaded'); }
            }});
            updateRoutes();
            if (state !== 'running') throw new Error(error || 'SteamVR routing could not start');
            native.enable(1);
            poll = setInterval(updateRoutes, 16);
            heartbeat(seconds);
        } catch (failure) { error = String(failure); deactivate(); throw failure; }
    },
    heartbeat, deactivate,
    status() { const snapshot = refreshNative();
        for (let side = 0; side < 2; side++) activeSides[side] = state === 'running' && snapshot.fault === 0 &&
            Boolean(sides[side]?.routeActive) && submissionAge[side] < 500;
        return {state, error, interfaceVersion, activeSides, skeletonSubmissions: submissions,
        callbackBackend: 'native', nativeFault: snapshot.fault, nativePoseCalls: snapshot.poses,
        callbacks: calls, results: resultCounts, routes, restored}; },
    dispose: deactivate
};
