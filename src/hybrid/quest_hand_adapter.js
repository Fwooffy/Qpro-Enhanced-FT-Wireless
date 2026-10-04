'use strict';
// Mono metadata gives us method/field identities; no managed address is guessed.
const profile = globalThis.QPRO_PROFILE.monoLayout;
const mono = Process.getModuleByName('libmonosgen-2.0.so');
const api = (name, result, args) => new NativeFunction(mono.getExportByName(name), result, args);
const rootDomain = api('mono_get_root_domain', 'pointer', [])();
const attachThread = api('mono_thread_attach', 'pointer', ['pointer']);
const detachThread = api('mono_thread_detach', 'void', ['pointer']);
const findAssembly = api('mono_assembly_loaded', 'pointer', ['pointer']);
const assemblyName = api('mono_assembly_name_new', 'pointer', ['pointer']);
const freeName = api('mono_assembly_name_free', 'void', ['pointer']);
const imageOf = api('mono_assembly_get_image', 'pointer', ['pointer']);
const findClass = api('mono_class_from_name', 'pointer', ['pointer', 'pointer', 'pointer']);
const methodOf = api('mono_class_get_method_from_name', 'pointer', ['pointer', 'pointer', 'int']);
const compile = api('mono_compile_method', 'pointer', ['pointer']);
const fieldOf = api('mono_class_get_field_from_name', 'pointer', ['pointer', 'pointer']);
const offsetOf = api('mono_field_get_offset', 'uint32', ['pointer']);
const parentOf = api('mono_class_get_parent', 'pointer', ['pointer']);
const className = api('mono_class_get_name', 'pointer', ['pointer']);
const vtableOf = api('mono_class_vtable', 'pointer', ['pointer', 'pointer']);
const staticValue = api('mono_field_static_get_value', 'void', ['pointer', 'pointer', 'pointer']);
const invoke = api('mono_runtime_invoke', 'pointer', ['pointer', 'pointer', 'pointer', 'pointer']);
const pinObject = api('mono_gchandle_new', 'uint32', ['pointer', 'int']);
const freePin = api('mono_gchandle_free', 'void', ['uint32']);
let state = 'idle', error = null, hooks = [], pins = [], lease = null;
let restoreTimer = null, hmd = null, originals = null, frames = 0;
let native = null, cleanupTimer = null, cleanupDeadline = 0;
const validFrames = [0, 0];
let lastValid = [0, 0];
const fingerOffsets = [profile.leftFingerOffset, profile.rightFingerOffset];
const HAND_CALLBACKS = `
#include <gum/guminterceptor.h>
#include <glib.h>
typedef struct { GMutex lock; guint phase, hands, updates, dispatch; } HandState;
typedef struct { gpointer owner, result; guint counted; } HandInvocation;
extern HandState shared;
extern guint initialized;
extern void on_hand(gpointer owner, gpointer result);
extern void on_update(gpointer owner);
guint state_size(void) { return sizeof(HandState); }
guint invocation_size(void) { return sizeof(HandInvocation); }
gboolean initialize_state(guint capacity) {
    if (capacity < sizeof(HandState)) return FALSE;
    shared.lock.p = NULL; g_mutex_init(&shared.lock);
    shared.phase = shared.hands = shared.updates = shared.dispatch = 0;
    initialized = 1; return TRUE;
}
void set_phase(guint phase) {
    g_mutex_lock(&shared.lock);
    // A late activation call must not reopen work after Stop won the lock.
    if (phase != 2 || shared.phase == 1) shared.phase = phase;
    g_mutex_unlock(&shared.lock);
}
guint pending(void) {
    guint value;
    g_mutex_lock(&shared.lock); value = shared.hands + shared.updates; g_mutex_unlock(&shared.lock);
    return value;
}
guint hand_pending(void) {
    guint value;
    g_mutex_lock(&shared.lock); value = shared.hands; g_mutex_unlock(&shared.lock);
    return value;
}
void updateEnter(GumInvocationContext * ic) {
    gboolean dispatch = FALSE;
    gpointer owner = gum_invocation_context_get_nth_argument(ic, 0);
    g_mutex_lock(&shared.lock);
    if (!shared.dispatch && (shared.phase == 1 || (shared.phase == 3 && shared.hands == 0))) {
        shared.dispatch = 1; shared.updates++; dispatch = TRUE;
    }
    g_mutex_unlock(&shared.lock);
    if (!dispatch) return;
    // No native lock is held while JavaScript invokes managed methods.
    on_update(owner);
    g_mutex_lock(&shared.lock); shared.updates--; shared.dispatch = 0; g_mutex_unlock(&shared.lock);
}
void handEnter(GumInvocationContext * ic) {
    HandInvocation * call = GUM_IC_GET_INVOCATION_DATA(ic, HandInvocation);
    call->counted = 0; call->owner = call->result = NULL;
    g_mutex_lock(&shared.lock);
    if (shared.phase == 2) {
        shared.hands++; call->counted = 1;
        call->owner = gum_invocation_context_get_nth_argument(ic, 0);
        call->result = gum_invocation_context_get_nth_argument(ic, 1);
    }
    g_mutex_unlock(&shared.lock);
}
void handLeave(GumInvocationContext * ic) {
    HandInvocation * call = GUM_IC_GET_INVOCATION_DATA(ic, HandInvocation);
    gpointer owner, result;
    gboolean forward;
    if (!call->counted) return;
    // A reentrant managed call can move Gum's invocation-data array. Copy
    // pointers and clear the flag before calling JavaScript; do not retain it.
    owner = call->owner; result = call->result; call->counted = 0;
    g_mutex_lock(&shared.lock); forward = shared.phase == 2; g_mutex_unlock(&shared.lock);
    if (forward) on_hand(owner, result);
    g_mutex_lock(&shared.lock); shared.hands--; g_mutex_unlock(&shared.lock);
}
void finalize(void) {
    if (!initialized) return;
    g_mutex_clear(&shared.lock); initialized = 0;
}
`;
function prepareNative() {
    if (native !== null) return native;
    const storage = Memory.alloc(128), initialized = Memory.alloc(4);
    initialized.writeU32(0);
    const handCallback = new NativeCallback((owner, result) => {
        if (state !== 'running') return;
        try {
            for (let side = 0; side < 2; side++) {
                if (state !== 'running') return;
                // Keep the existing NativeFunction exception handling and both
                // conversions on the intercepted managed application thread.
                const valid = bindings.convert(owner, side, result.add(fingerOffsets[side])) !== 0;
                if (state !== 'running') return;
                result.add(side).writeU8(valid ? 1 : 0);
                if (valid) { validFrames[side]++; lastValid[side] = Date.now(); }
            }
            frames++;
        } catch (failure) { requestStop(failure); }
    }, 'void', ['pointer', 'pointer']);
    const updateCallback = new NativeCallback(owner => {
        try {
            if (state === 'restoring') { restore(); return; }
            if (state !== 'starting') return;
            hmd = owner; pins.push(pinObject(hmd, 1));
            originals = {hmd: hmd.add(bindings.hmdOffset).readU8(), shared: bindings.shared.add(bindings.sharedOffset).readU8()};
            setBoolean(bindings.sharedSetter, bindings.shared, 1);
            setBoolean(bindings.hmdSetter, hmd, 1);
            if (hmd.add(bindings.hmdOffset).readU8() !== 1 || bindings.shared.add(bindings.sharedOffset).readU8() !== 1)
                throw new Error('Managed multimodal activation did not verify');
            // Stop may arrive while a cooperative managed call releases the
            // JS lock. Finish activation, then restore without reopening input.
            if (state === 'starting') { state = 'running'; native.setPhase(2); }
        } catch (failure) { requestStop(failure); }
    }, 'void', ['pointer']);
    const module = new CModule(HAND_CALLBACKS, {shared: storage, initialized,
        on_hand: handCallback, on_update: updateCallback}, {toolchain: 'internal'});
    const bind = (name, result, args = []) => new NativeFunction(module[name], result, args,
        {scheduling: 'cooperative', traps: 'none'});
    try {
        const size = bind('state_size', 'uint')(), invocationSize = bind('invocation_size', 'uint')();
        if (Process.pointerSize !== 8 || size < 1 || size > 128 || invocationSize < 1 || invocationSize > 1024)
            throw new Error('Native optical callback layout is unsupported');
        if (!bind('initialize_state', 'int', ['uint'])(128)) throw new Error('Native optical callbacks did not initialize');
        native = {module, storage, initialized, handCallback, updateCallback,
            setPhase: bind('set_phase', 'void', ['uint']), pending: bind('pending', 'uint'),
            handPending: bind('hand_pending', 'uint'), attached: false};
        return native;
    } catch (failure) { module.dispose(); throw failure; }
}
function restorationFailure(failure) {
    error = String(failure); state = 'restore-failed';
    try { native?.setPhase(0); } catch (disableFailure) { error += '; ' + String(disableFailure); }
}
function requestStop(failure) {
    error = String(failure);
    try { deactivate(); } catch (stopFailure) { restorationFailure(error + '; ' + String(stopFailure)); }
}

function requirePointer(pointer, message) {
    if (pointer.isNull()) throw new Error(message);
    return pointer;
}
function managed(action) {
    const thread = attachThread(rootDomain);
    try { return action(); } finally { detachThread(thread); }
}
function classFor(assembly, namespace, name) {
    const descriptor = assemblyName(Memory.allocUtf8String(assembly));
    try {
        const loaded = requirePointer(findAssembly(descriptor), 'Managed assembly is not loaded: ' + assembly);
        return requirePointer(findClass(imageOf(loaded), Memory.allocUtf8String(namespace), Memory.allocUtf8String(name)), 'Managed class is missing: ' + name);
    } finally { freeName(descriptor); }
}
function method(klass, name, argumentsCount) {
    return requirePointer(methodOf(klass, Memory.allocUtf8String(name), argumentsCount), 'Managed method is missing: ' + name);
}
function field(klass, name) {
    return requirePointer(fieldOf(klass, Memory.allocUtf8String(name)), 'Managed field is missing: ' + name);
}
let bindings = null;
function resolve() {
    if (bindings !== null) return bindings;
    return managed(() => {
        const klass = classFor(profile.assembly, profile.namespace, profile.class);
        const settings = classFor(profile.settingsAssembly, profile.settingsNamespace, profile.settingsClass);
        let base = settings;
        for (let count = 0; count < 12; count++) {
            if (className(base).readUtf8String() === 'SettingsBase`1') break;
            base = requirePointer(parentOf(base), 'Settings singleton base is missing');
        }
        if (className(base).readUtf8String() !== 'SettingsBase`1') throw new Error('Settings inheritance is unsupported');
        const sharedSlot = Memory.alloc(Process.pointerSize);
        staticValue(vtableOf(rootDomain, base), field(base, '<Default>k__BackingField'), sharedSlot);
        const shared = requirePointer(sharedSlot.readPointer(), 'Settings singleton is unavailable');
        const get = name => requirePointer(compile(method(klass, name, name === 'ConvertFingerState' ? 2 : 1)), 'Managed method did not compile');
        bindings = {
            update: get('Update'), handState: get('GetHandState'),
            convert: new NativeFunction(get('ConvertFingerState'), 'int', ['pointer', 'int', 'pointer']),
            hmdSetter: method(klass, 'set_UseMultiModalInput', 1),
            sharedSetter: method(settings, 'set_UseMultiModal', 1), shared,
            hmdOffset: offsetOf(field(klass, '_useMultiModalInput')),
            sharedOffset: offsetOf(field(settings, '_useMultiModal'))
        };
        if (bindings.hmdOffset < Process.pointerSize * 2 || bindings.sharedOffset < Process.pointerSize * 2)
            throw new Error('Managed field offsets are unsupported');
        return bindings;
    });
}
function setBoolean(setter, object, value) {
    const argument = Memory.alloc(4); argument.writeS32(value);
    const parameters = Memory.alloc(Process.pointerSize); parameters.writePointer(argument);
    const exception = Memory.alloc(Process.pointerSize); exception.writePointer(ptr(0));
    invoke(setter, object, parameters, exception);
    if (!exception.readPointer().isNull()) throw new Error('Managed multimodal setter raised an exception');
}
function release() {
    if (lease !== null) { clearTimeout(lease); lease = null; }
    if (restoreTimer !== null) { clearTimeout(restoreTimer); restoreTimer = null; }
    if (native !== null) native.setPhase(0);
    if (cleanupTimer !== null) return;
    cleanupDeadline = Date.now() + 5000;
    function finish() {
        cleanupTimer = null;
        try {
            if (native !== null && native.pending() !== 0) {
                if (Date.now() >= cleanupDeadline) throw new Error('Native optical callbacks did not drain');
                cleanupTimer = setTimeout(finish, 25); return;
            }
            while (hooks.length) { hooks[0].detach(); hooks.shift(); }
            Interceptor.flush();
            managed(() => { while (pins.length) { freePin(pins[0]); pins.shift(); } });
            // Keep native code, storage and callbacks strongly reachable until
            // Frida unloads the script after its own native Interceptor drain.
            state = 'stopped';
        } catch (failure) { restorationFailure(failure); }
    }
    cleanupTimer = setTimeout(finish, 0);
}
function restore() {
    try {
        if (native !== null && native.handPending() !== 0) return;
        if (originals === null) { release(); return; }
        setBoolean(bindings.hmdSetter, hmd, originals.hmd);
        setBoolean(bindings.sharedSetter, bindings.shared, originals.shared);
        if (hmd.add(bindings.hmdOffset).readU8() !== originals.hmd ||
            bindings.shared.add(bindings.sharedOffset).readU8() !== originals.shared)
            throw new Error('Managed settings restoration did not verify');
        originals = null;
        release();
    } catch (failure) { restorationFailure(failure); }
}
function deactivate() {
    if (['stopped', 'restore-failed', 'restoring'].includes(state)) return;
    if (state === 'idle') { state = 'stopped'; return; }
    state = 'restoring';
    if (native !== null) native.setPhase(3);
    if (lease !== null) { clearTimeout(lease); lease = null; }
    // Prefer the existing VD Update thread. A stalled update loop uses a Mono-
    // attached fallback so app-owned fields do not remain changed after a drop.
    if (restoreTimer === null) {
        const deadline = Date.now() + 5000;
        function fallback() {
            restoreTimer = null;
            try {
                if (native !== null && native.pending() !== 0) {
                    if (Date.now() >= deadline) throw new Error('Native optical callbacks did not drain before restoration');
                    restoreTimer = setTimeout(fallback, 25); return;
                }
                managed(restore);
            } catch (failure) { restorationFailure(failure); }
        }
        restoreTimer = setTimeout(fallback, 2500);
    }
}
function heartbeat(seconds) {
    if (!Number.isFinite(seconds) || seconds < 1 || seconds > 30 || !['starting', 'running'].includes(state))
        throw new Error('Invalid optical hand lease');
    if (lease !== null) clearTimeout(lease);
    lease = setTimeout(deactivate, seconds * 1000);
}
rpc.exports = {
    validate() { resolve(); prepareNative(); return {compatible: true, managedMetadata: true,
        nativeCallbacks: true, fridaRuntime: Script.runtime}; },
    activate(seconds) {
        if (state !== 'idle') throw new Error('Optical hand adapter is already used');
        resolve(); const callbacks = prepareNative(); state = 'starting';
        try {
            pins.push(managed(() => pinObject(bindings.shared, 1)));
            callbacks.setPhase(1);
            hooks.push(Interceptor.attach(bindings.update, {onEnter: callbacks.module.updateEnter}));
            callbacks.attached = true;
            hooks.push(Interceptor.attach(bindings.handState, {
                onEnter: callbacks.module.handEnter, onLeave: callbacks.module.handLeave}));
            heartbeat(seconds);
        } catch (failure) { requestStop(failure); throw failure; }
    },
    heartbeat, deactivate,
    status() { return {state, error, frames, validFrames, freshSides: lastValid.map(time => Date.now() - time < 500),
        nativeCallbacks: true, fridaRuntime: Script.runtime}; },
    dispose() { deactivate(); return new Promise((accept, reject) => {
        const timer = setInterval(() => {
            if (state === 'stopped') { clearInterval(timer); accept(); }
            if (state === 'restore-failed') { clearInterval(timer); reject(new Error(error)); }
        }, 50);
    }); }
};
