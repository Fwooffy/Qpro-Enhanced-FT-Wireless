'use strict';
// The host admits this adapter only for the exact driver fingerprint in the
// compatibility profile. Every live object and public interface is rechecked.
const configuration = globalThis.QPRO_PROFILE;
const layout = configuration.pcLayout;
let state = 'idle', error = null, driver = null, input = null, updateAddress = null;
let updateHooks = [], updateTargets = [], poseHook = null, observer = null, cleanupScheduled = false;
let poll = null, lease = null, interfaceVersion = null;
const sides = [null, null], submissions = [0, 0], lastSubmission = [0, 0];
const activeSides = [false, false];
let restored = 0;
const calls = {seen: 0, inputOwner: 0, matched: 0, successful: 0, invalidBones: 0, poseCopies: 0,
    sources: {'vd-original': 0}};
const resultCounts = {};
const routes = [0, 1].map(() => ({reason: 'not-running', physical: false, optical: false, handle: false}));

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
function validateBones(bones, count) {
    if (count !== configuration.boneCount || !readable(bones, count * 32)) return false;
    // Copy once across Frida's memory bridge, then inspect the same 248 values
    // in JavaScript. Individual readFloat calls block the native driver thread.
    const buffer = bones.readByteArray(count * 32);
    if (buffer === null || buffer.byteLength !== count * 32) return false;
    const values = new Float32Array(buffer);
    for (const value of values) {
        if (!Number.isFinite(value)) return false;
    }
    return true;
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
    if (state === 'stopped') return;
    state = 'restoring';
    const failures = [];
    if (poll !== null) { clearInterval(poll); poll = null; }
    if (lease !== null) { clearTimeout(lease); lease = null; }
    const remainingHooks = [];
    for (const hook of updateHooks) {
        try { hook.detach(); } catch (failure) { failures.push(String(failure)); remainingHooks.push(hook); }
    }
    updateHooks = remainingHooks;
    if (poseHook !== null) {
        try { poseHook.detach(); poseHook = null; } catch (failure) { failures.push(String(failure)); }
    }
    if (observer !== null) {
        try { observer.detach(); observer = null; } catch (failure) { failures.push(String(failure)); }
    }
    for (const record of sides) {
        try { restore(record); } catch (failure) { failures.push(String(failure)); }
    }
    activeSides.fill(false);
    if (failures.length) { error = failures.join('; '); state = 'restore-failed'; }
    else state = 'stopped';
}
function fail(failure) {
    error = String(failure);
    if (cleanupScheduled || state === 'stopped' || state === 'restore-failed') return;
    // Native callbacks must return before their listeners are detached or
    // shared driver fields are restored. Stop new routing writes immediately.
    state = 'failing'; cleanupScheduled = true;
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
        for (let side = 0; side < 2; side++) {
            let record = sides[side];
            if (record === null || !sameObjects(record)) record = sides[side] = resolveSide(side);
            if (record === null) {
                routes[side] = {reason: 'controller-unavailable', physical: false, optical: false, handle: false};
                activeSides[side] = false; continue;
            }
            const data = record.controller.add(layout.dataPointer).readPointer();
            if (data.isNull()) {
                routes[side] = {reason: 'controller-data-unavailable', physical: false, optical: false, handle: false};
                restore(record); record.physical = false; activeSides[side] = false; continue;
            }
            requireObject(data, layout.opticalFlags + 2, 'VD controller frame');
            const frame = data.readPointer();
            if (frame.isNull()) {
                routes[side] = {reason: 'tracking-frame-unavailable', physical: false, optical: false, handle: false};
                restore(record); record.physical = false; activeSides[side] = false; continue;
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
            activeSides[side] = enabled && Date.now() - lastSubmission[side] < 500;
            routes[side] = {reason: !physical ? 'controller-not-held' : optical !== 1 ? 'optical-hand-unavailable' :
                handle.equals(0) ? 'skeleton-handle-unavailable' : activeSides[side] ? 'active' : 'waiting-for-skeleton',
                physical, optical: optical === 1, handle: !handle.equals(0)};
        }
    } catch (failure) { fail(failure); }
}
rpc.exports = {
    validate() {
        resolveInterfaces(); sides[0] = resolveSide(0); sides[1] = resolveSide(1);
        return {compatible: true, interfaceVersion, boneCount: configuration.boneCount,
            controllersPresent: sides.map(record => record !== null), skeletonObservers: updateTargets.length};
    },
    activate(seconds) {
        if (state !== 'idle') throw new Error('Skeleton adapter is already used');
        const poseAddress = resolveInterfaces(); state = 'running';
        try {
            // Observe the saved public API implementation without replacing it;
            // every driver's input arguments and result pass through unchanged.
            for (const target of updateTargets) updateHooks.push(Interceptor.attach(target.address, {
                onEnter(args) {
                    this.record = null; this.valid = false;
                    if (state !== 'running') return;
                    calls.seen++;
                    calls.sources[target.source]++;
                    try {
                        if (args[0].equals(input)) calls.inputOwner++;
                        const handle = uint64(args[1].toString());
                        const record = sides.find(candidate => candidate !== null && candidate.handle.equals(handle) &&
                            sameObjects(candidate));
                        if (record === undefined) return;
                        calls.matched++;
                        this.record = record;
                        this.valid = validateBones(args[3], args[4].toUInt32());
                        if (!this.valid) calls.invalidBones++;
                    } catch (failure) { fail(failure); }
                },
                onLeave(result) {
                    if (state !== 'running') return;
                    try {
                        const code = result.toInt32();
                        resultCounts[code] = (resultCounts[code] || 0) + 1;
                        if (code === 0) calls.successful++;
                        if (target.source === 'vd-original' && code === 0 && this.record?.routeActive && this.valid && sameObjects(this.record)) {
                            submissions[this.record.side]++; lastSubmission[this.record.side] = Date.now();
                        }
                    } catch (failure) { fail(failure); }
                }
            }));
            poseHook = Interceptor.attach(poseAddress, {onEnter(args) {
                if (state !== 'running') return;
                try {
                    const device = args[1].toUInt32();
                    const record = sides.find(candidate => candidate !== null && candidate.handDevice === device && sameObjects(candidate));
                    if (record === undefined || !record.physical || !record.routeActive) return;
                    // Public DriverPose_t validity bytes for the admitted win64 ABI.
                    const pose = args[2];
                    if (args[3].toUInt32() !== 280 || !readable(pose, 280)) throw new Error('DriverPose_t ABI mismatch');
                    // OpenVR takes a const reference. Redirect this invocation
                    // to a retained copy rather than editing VD's own pose.
                    this.poseCopy = Memory.alloc(280);
                    Memory.copy(this.poseCopy, pose, 280);
                    this.poseCopy.add(276).writeU8(0); this.poseCopy.add(279).writeU8(0);
                    args[2] = this.poseCopy; calls.poseCopies++;
                } catch (failure) { fail(failure); }
            }, onLeave() { this.poseCopy = null; }});
            observer = Process.attachModuleObserver({onRemoved(module) {
                if (module.base.equals(driver.base)) fail('VD driver unloaded');
            }});
            poll = setInterval(updateRoutes, 16);
            heartbeat(seconds);
        } catch (failure) { error = String(failure); deactivate(); throw failure; }
    },
    heartbeat, deactivate,
    status() { return {state, error, interfaceVersion, activeSides, skeletonSubmissions: submissions,
        callbacks: calls, results: resultCounts, routes, restored}; },
    dispose: deactivate
};
