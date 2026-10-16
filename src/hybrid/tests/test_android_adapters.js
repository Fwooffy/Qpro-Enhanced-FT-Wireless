'use strict';
// JavaScript/native boundary and lifetime tests. The C callback bodies require
// a real Frida CModule harness; these fixtures never attach to any OS process.
const fs = require('fs');
const vm = require('vm');
const path = require('path');
const assert = require('assert');
const directory = path.resolve(__dirname, '..');
const profile = JSON.parse(fs.readFileSync(path.join(directory, 'compatibility.json')));
let passed = 0;
function test(name, action) { action(); passed++; }

class Pointer {
    constructor(value, memory) { this.value = Number(value); this.memory = memory; }
    add(offset) { return new Pointer(this.value + offset, this.memory); }
    isNull() { return this.value === 0; }
    readPointer() { return new Pointer(this.memory.get(this.value) || 0, this.memory); }
    writePointer(pointer) { this.memory.set(this.value, pointer.value); }
    readU8() { return Number(this.memory.get(this.value) || 0); }
    writeU8(value) { this.memory.set(this.value, value); }
    readU32() { return Number(this.memory.get(this.value) || 0); }
    readUtf8String() { return this.memory.get(this.value) || ''; }
    writeU32(value) { this.memory.set(this.value, value); }
    writeS32(value) { this.memory.set(this.value, value); }
    readU64() { const value = Number(this.memory.get(this.value) || 0); return {toNumber: () => value}; }
    toString() { return '0x' + this.value.toString(16); }
}
class NativeSymbol {
    constructor(name, call = () => {}) { this.name = name; this.call = call; }
}
function fixture(kind, options = {}) {
    const memory = new Map(), events = [], timers = new Map(), modules = [];
    const address = value => new Pointer(value, memory);
    const hmd = address(10000), shared = address(20000), output = address(30000);
    let singleton = options.singleton || shared.value;
    let nextMemory = 40000, nextTimer = 0, clock = 1000, nextPin = 0, detachCalls = 0, freeCalls = 0;
    const calls = [], invokes = [], pinObjects = new Map();
    const monoFunctions = {
        mono_get_root_domain: () => address(9000),
        mono_thread_attach: () => { events.push('mono-attach'); return address(9100); },
        mono_thread_detach: () => {
            events.push('mono-detach'); detachCalls++;
            if (options.onThreadDetach) options.onThreadDetach(sandbox, detachCalls);
            if (detachCalls === options.detachFailure) throw Error('Mono detach refused');
        },
        mono_gchandle_new: object => {
            events.push('pin:' + object.value);
            if (options.onPinNew) options.onPinNew(sandbox, object, nextPin + 1);
            if (options.pinFailure === true || options.pinFailure === nextPin + 1) return 0;
            pinObjects.set(++nextPin, nextPin === 1 && options.pinTarget ? options.pinTarget : object.value); return nextPin;
        },
        mono_gchandle_get_target: handle => address(options.targetFailure === handle ? 0 : pinObjects.get(handle) || 0),
        mono_field_static_get_value: (_table, _field, slot) => slot.writePointer(address(singleton)),
        mono_gchandle_free: handle => {
            events.push('free:' + handle); freeCalls++;
            if (options.onPinFree) options.onPinFree(sandbox, freeCalls);
        },
        mono_runtime_invoke: (setter, object, parameters, exception) => {
            const value = parameters.readPointer().readU32();
            invokes.push({setter: setter.value, object: object.value, value});
            if (options.onSetter) options.onSetter(sandbox, invokes.length, value);
            if (options.setterFailure && invokes.length === options.setterFailure) {
                exception.writePointer(address(1)); return address(0);
            }
            object.add(setter.value === 70 ? 64 : 72).writeU8(value);
            return address(0);
        }
    };
    const methods = {Update: 1000, GetHandState: 2000, ConvertFingerState: 3000,
        set_UseMultiModalInput: 70, set_UseMultiModal: 80};
    const signatures = new Map(), types = new Map();
    function metadataType(type) {
        const result = address(nextMemory); nextMemory += 16; types.set(result.value, type); return result;
    }
    for (const [name, id] of Object.entries(methods)) {
        const setter = name.startsWith('set_');
        const value = {instance: true, result: {kind: name === 'ConvertFingerState' ? 2 : 1},
            parameters: setter ? [{kind: 2}] : name === 'ConvertFingerState' ? [{kind: 8}, {kind: 17, byref: true}]
                : [{kind: name === 'GetHandState' ? 17 : 12, byref: name === 'GetHandState'}]};
        Object.assign(value, options.signatures?.[name]); signatures.set(id + 100000, value);
    }
    Object.assign(monoFunctions, {
        mono_assembly_name_new: () => address(12), mono_assembly_name_free: () => {},
        mono_assembly_loaded: () => address(13), mono_assembly_get_image: () => address(14),
        mono_class_from_name: (_image, _namespace, name) => address(name.readUtf8String() === 'OpenXRHMD' ? 50 : 60),
        mono_class_get_parent: () => address(61),
        mono_class_get_name: klass => {
            const result = address(nextMemory); nextMemory += 32;
            memory.set(result.value, klass.value === 61 ? 'SettingsBase`1' : 'SharedUserSettings'); return result;
        },
        mono_class_get_field_from_name: (_klass, name) => address(name.readUtf8String() === '_useMultiModalInput' ? 64
            : name.readUtf8String() === '_useMultiModal' ? 72 : 11),
        mono_class_vtable: () => address(10), mono_field_get_offset: field => field.value,
        mono_field_get_type: () => metadataType({kind: options.fieldKind || 2}),
        mono_field_get_flags: field => field.value === 11 ? (options.nonstaticSingleton ? 0 : 16) : (options.staticBoolean ? 16 : 0),
        mono_class_instance_size: () => options.instanceSize || 512,
        mono_class_get_method_from_name: (_klass, name) => address(methods[name.readUtf8String()] || 0),
        mono_compile_method: found => found,
        mono_method_signature: found => address(found.value + 100000),
        mono_signature_is_instance: signature => Number(signatures.get(signature.value).instance),
        mono_signature_get_param_count: signature => signatures.get(signature.value).parameters.length,
        mono_signature_get_return_type: signature => metadataType(signatures.get(signature.value).result),
        mono_signature_get_params: (signature, iterator) => {
            const index = iterator.readU32(); iterator.writeU32(index + 1);
            return metadataType(signatures.get(signature.value).parameters[index]);
        },
        mono_type_get_type: type => types.get(type.value).kind,
        mono_type_is_byref: type => Number(Boolean(types.get(type.value).byref)),
        mono_type_get_class: () => address(62), mono_class_is_enum: () => 1,
        mono_class_enum_basetype: () => metadataType({kind: options.enumBaseKind || 8})
    });
    function CModule(source, symbols, moduleOptions) {
        events.push('compile');
        if (options.compileFailure) throw new Error('CModule unavailable');
        assert.equal(moduleOptions.toolchain, 'internal');
        const query = source.includes('QueryState');
        const record = {source, symbols, phase: 0, pendingValue: 0, handPendingValue: 0,
            errorValue: 0, queries: 0, changes: 0, ids: {}, attached: false, disposed: false};
        modules.push(record);
        const emit = (name, call) => this[name] = new NativeSymbol(name, call);
        emit('state_size', () => options.stateSize || (query ? 64 : 24));
        emit('invocation_size', () => options.invocationSize || 32);
        emit('initialize_state', capacity => { events.push('initialize:' + capacity); return options.initializeFailure ? 0 : 1; });
        emit('pending', () => record.pendingValue);
        if (query) {
            emit('snapshot_size', () => 32); emit('id_size', () => 16);
            emit('start', caller => { record.caller = caller; record.enabled = true; events.push('enable'); });
            emit('disable', () => { record.enabled = false; events.push('disable'); });
            emit('onEnter'); emit('onLeave');
            emit('snapshot', (buffer, capacity) => {
                const entries = Object.entries(record.ids), size = 32 + entries.length * 16;
                if (capacity < size) return size;
                memory.set(buffer.value, record.queries); memory.set(buffer.value + 8, record.changes);
                buffer.add(16).writeU32(record.errorValue); buffer.add(20).writeU32(record.pendingValue);
                buffer.add(24).writeU32(entries.length);
                entries.forEach(([id, count], index) => {
                    buffer.add(32 + index * 16).writeU32(Number(id)); memory.set(buffer.value + 40 + index * 16, count);
                });
                return size;
            });
        } else {
            emit('set_phase', phase => { record.phase = phase; events.push('phase:' + phase); });
            emit('hand_pending', () => record.handPendingValue);
            emit('updateEnter'); emit('handEnter'); emit('handLeave');
        }
        this.dispose = () => { record.disposed = true; events.push('dispose'); };
        record.module = this;
    }
    const sandbox = vm.createContext({
        QPRO_PROFILE: profile, rpc: {exports: {}}, Script: {runtime: 'QJS'}, CModule,
        Process: {arch: 'arm64', pointerSize: 8, getModuleByName: () => ({
            getExportByName: name => new NativeSymbol(name, monoFunctions[name] || (() => address(0))),
            enumerateRanges: () => []
        })},
        NativeFunction: function(pointer, result, args, nativeOptions) {
            if (nativeOptions) {
                assert.equal(nativeOptions.scheduling, 'cooperative'); assert.equal(nativeOptions.traps, 'none');
            }
            return pointer.call || (() => 1);
        },
        NativeCallback: function(callback, result, args) { return {callback, result, args}; },
        Memory: {
            alloc: size => { const block = address(nextMemory); nextMemory += size + 16; return block; },
            allocUtf8String: value => { const block = address(nextMemory); nextMemory += value.length + 16; memory.set(block.value, value); return block; }
        },
        ptr: address, Date: {now: () => clock},
        Interceptor: {
            attach: (target, callbacks) => {
                assert.ok(callbacks.onEnter instanceof NativeSymbol);
                if (callbacks.onLeave) assert.ok(callbacks.onLeave instanceof NativeSymbol);
                events.push('attach:' + target.value);
                return {detach: () => events.push('detach:' + target.value)};
            },
            flush: () => events.push('flush')
        },
        setTimeout: (callback, delay) => { timers.set(++nextTimer, {callback, delay}); return nextTimer; },
        clearTimeout: id => timers.delete(id),
        setInterval: () => { throw Error('No interval is needed in these tests'); }, clearInterval() {}
    });
    vm.runInContext(fs.readFileSync(path.join(directory, kind === 'query' ? 'quest_controller_adapter.js' : 'quest_hand_adapter.js'), 'utf8'), sandbox);
    if (kind === 'query') {
        sandbox.testResolution = {callee: address(1000), caller: address(1016)};
        vm.runInContext('resolution = testResolution;', sandbox);
    } else {
        sandbox.testBindings = {
            update: address(1000), handState: address(2000), shared, sharedVtable: address(10), sharedField: address(11),
            hmdOffset: 64, sharedOffset: 72, hmdSetter: address(70), sharedSetter: address(80),
            convert: (owner, side, destination) => {
                calls.push({owner: owner.value, side, destination: destination.value});
                if (options.onConvert) options.onConvert(sandbox, calls.length);
                if (options.convertFailure) throw Error('conversion refused');
                return options.validSides ? Number(options.validSides[side]) : 1;
            }
        };
        if (!options.metadata) vm.runInContext('bindings = testBindings;', sandbox);
    }
    function runTimer(delay) {
        const found = [...timers].find(([, entry]) => entry.delay === delay);
        assert.ok(found, 'Expected timer with delay ' + delay);
        timers.delete(found[0]); found[1].callback();
    }
    function dispatch(name, ...args) {
        const record = modules[0];
        record.pendingValue++;
        try { record.symbols[name].callback(...args); } finally { record.pendingValue--; }
    }
    return {sandbox, api: sandbox.rpc.exports, memory, events, modules, hmd, shared, output, calls, invokes,
        runTimer, dispatch, advance: milliseconds => { clock += milliseconds; }, setSingleton: value => { singleton = value; }};
}

for (const kind of ['query', 'hands']) {
    test(kind + ' compiles and checks layout before attaching', () => {
        const f = fixture(kind), result = f.api.validate();
        assert.equal(result.compatible, true); assert.equal(result.nativeCallbacks, true);
        assert.equal(result.fridaRuntime, 'QJS');
        assert.equal(f.modules.length, 1); assert.equal(f.events.some(event => event.startsWith('attach:')), false);
    });
    for (const options of [{compileFailure: true}, {stateSize: 4096}, {invocationSize: 2048}, {initializeFailure: true}]) {
        test(kind + ' refuses unavailable native preparation', () => {
            const f = fixture(kind, options);
            assert.throws(() => f.api.validate());
            assert.equal(f.events.some(event => event.startsWith('attach:')), false);
            if (!options.compileFailure) assert.equal(f.modules[0].disposed, true);
        });
    }
}

test('managed preflight validates actual signature metadata without pinning or hooks', () => {
    const f = fixture('hands', {metadata: true});
    assert.equal(f.api.validate().managedMetadata, true);
    assert.equal(f.events.some(event => event.startsWith('pin:') || event.startsWith('attach:')), false);
});
for (const [name, signature] of [
    ['Update', {instance: false}], ['GetHandState', {parameters: [{kind: 12}]}],
    ['ConvertFingerState', {result: {kind: 13}}],
    ['ConvertFingerState', {parameters: [{kind: 10}, {kind: 17, byref: true}]}],
    ['ConvertFingerState', {parameters: [{kind: 8}, {kind: 12}]}],
    ['set_UseMultiModalInput', {result: {kind: 2}}],
    ['set_UseMultiModal', {parameters: [{kind: 8}]}]
]) {
    test('managed preflight refuses incompatible ' + name + ' metadata', () => {
        const f = fixture('hands', {metadata: true, signatures: {[name]: signature}});
        assert.throws(() => f.api.validate(), /signature is unsupported/);
        assert.equal(f.events.some(event => event.startsWith('pin:') || event.startsWith('attach:')), false);
        assert.throws(() => f.api.activate(20), /signature is unsupported/);
    });
}
test('managed enum conversion requires a supported 32-bit underlying argument', () => {
    const signatures = {ConvertFingerState: {parameters: [{kind: 17}, {kind: 17, byref: true}]}};
    assert.equal(fixture('hands', {metadata: true, signatures}).api.validate().compatible, true);
    assert.throws(() => fixture('hands', {metadata: true, signatures, enumBaseKind: 10}).api.validate(), /signature is unsupported/);
});
for (const options of [{fieldKind: 8}, {staticBoolean: true}, {instanceSize: 72}, {nonstaticSingleton: true}]) {
    test('managed preflight refuses incompatible singleton or Boolean field metadata', () => {
        const f = fixture('hands', {metadata: true, ...options});
        assert.throws(() => f.api.validate(), /field layout is unsupported|singleton field is not static/);
        assert.equal(f.events.some(event => event.startsWith('pin:') || event.startsWith('attach:')), false);
        assert.throws(() => f.api.activate(20), /field layout is unsupported|singleton field is not static/);
    });
}
test('activation re-reads the singleton and uses the pinned handle target', () => {
    const f = fixture('hands', {metadata: true, pinTarget: 22000});
    f.api.validate();
    f.setSingleton(21000);
    assert.equal(f.events.some(event => event.startsWith('pin:')), false);
    f.api.activate(20);
    assert.ok(f.events.includes('pin:21000'));
    f.dispatch('on_update', f.hmd);
    assert.equal(f.invokes[0].object, 22000);
    assert.equal(f.memory.get(22000 + 72), 1);
    assert.equal(f.memory.get(f.shared.value + 72), undefined);
});
test('failed singleton pin refuses hooks and does not claim running input', () => {
    const f = fixture('hands', {pinFailure: true});
    assert.throws(() => f.api.activate(20), /pin was not created/);
    assert.equal(f.events.some(event => event.startsWith('attach:')), false);
    assert.notEqual(f.api.status().state, 'running');
});
test('failed pinned target retains and then releases its handle before claiming stopped', () => {
    const f = fixture('hands', {targetFailure: 1});
    assert.throws(() => f.api.activate(20), /Pinned Settings singleton is unavailable/);
    assert.equal(f.events.some(event => event.startsWith('attach:')), false);
    f.runTimer(2500); f.runTimer(0);
    assert.ok(f.events.includes('free:1'));
    assert.equal(f.api.status().state, 'stopped');
    assert.match(f.api.status().error, /Pinned Settings singleton/);
});
test('failed HMD pin starts cleanup before any managed input flag changes', () => {
    const f = fixture('hands', {pinFailure: 2}); f.api.activate(20);
    f.dispatch('on_update', f.hmd);
    assert.equal(f.invokes.length, 0);
    f.runTimer(2500); f.runTimer(0);
    assert.equal(f.api.status().state, 'stopped');
    assert.match(f.api.status().error, /HMD pin was not created/);
});
for (const boundary of ['onPinNew', 'onThreadDetach']) {
    test('Stop overlapping activation at ' + boundary + ' waits for handle ownership and never attaches late hooks', () => {
        let f;
        f = fixture('hands', {[boundary]: (_sandbox, _object, pin) => {
            // onThreadDetach receives its call count in the second slot.
            if (boundary === 'onPinNew' ? pin !== 1 : _object !== 1) return;
            f.api.deactivate(); f.runTimer(2500); f.runTimer(0);
            assert.equal(f.api.status().state, 'restoring');
            assert.equal(f.events.some(event => event.startsWith('free:')), false);
        }});
        f.api.activate(20);
        assert.equal(f.events.some(event => event.startsWith('attach:')), false);
        f.runTimer(25);
        assert.equal(f.api.status().state, 'stopped');
        assert.ok(f.events.includes('free:1'));
        assert.equal(f.invokes.length, 0);
    });
}
test('Stop during activation metadata preflight cannot reopen a stopped adapter', () => {
    let f;
    f = fixture('hands', {metadata: true, onThreadDetach: (_sandbox, count) => {
        if (count === 1) f.api.deactivate();
    }});
    f.api.activate(20);
    assert.equal(f.api.status().state, 'stopped');
    assert.equal(f.events.some(event => event.startsWith('pin:') || event.startsWith('attach:')), false);
    assert.equal(f.modules.length, 0);
});

test('query counters retain all controller IDs and native failures stop work', () => {
    const f = fixture('query'); f.api.activate(20);
    Object.assign(f.modules[0], {queries: 19, changes: 7, ids: {0: 3, 11: 16}});
    let status = f.api.status();
    assert.equal(status.queries, 19); assert.equal(status.changes, 7);
    assert.equal(JSON.stringify(status.controllerIds), '{"0":3,"11":16}');
    f.modules[0].errorValue = 2;
    status = f.api.status(); assert.equal(status.state, 'restoring');
    assert.match(status.error, /Unexpected controller query result/);
    assert.equal(f.modules[0].enabled, false);
    f.runTimer(0); assert.equal(f.api.status().state, 'stopped');
    assert.equal(f.modules[0].disposed, false);
});
test('query disables then drains before detach and retains used module', () => {
    const f = fixture('query'); f.api.activate(20); f.modules[0].pendingValue = 1;
    f.api.deactivate(); f.runTimer(0);
    assert.equal(f.events.some(event => event.startsWith('detach:')), false);
    f.modules[0].pendingValue = 0; f.runTimer(25);
    assert.equal(f.api.status().state, 'stopped'); assert.equal(f.modules[0].disposed, false);
    assert.ok(f.events.indexOf('disable') < f.events.indexOf('detach:1000'));
    assert.ok(f.events.indexOf('detach:1000') < f.events.indexOf('flush'));
});
test('query drain failure remains visible after status and repeated Stop', () => {
    const f = fixture('query'); f.api.activate(20); f.modules[0].pendingValue = 1;
    f.modules[0].errorValue = 2; f.api.deactivate(); f.advance(5000); f.runTimer(0);
    const status = f.api.status(); assert.equal(status.state, 'restore-failed');
    assert.match(status.error, /callbacks did not drain/);
    f.api.deactivate(); assert.equal(f.api.status().state, 'restore-failed');
    assert.equal(f.events.some(event => event.startsWith('detach:')), false);
    assert.equal(f.modules[0].disposed, false);
});
test('query lease expiry starts deferred cleanup', () => {
    const f = fixture('query'); f.api.activate(20); f.runTimer(20000);
    assert.equal(f.api.status().state, 'restoring'); f.runTimer(0);
    assert.equal(f.api.status().state, 'stopped');
});

test('hands preserve two conversions, validity and same-side freshness', () => {
    const f = fixture('hands', {validSides: [true, false]}); f.api.activate(20);
    f.dispatch('on_update', f.hmd); assert.equal(f.api.status().state, 'running');
    f.dispatch('on_hand', f.hmd, f.output);
    assert.equal(f.calls.length, 2);
    assert.equal(f.calls[0].destination, f.output.value + profile.monoLayout.leftFingerOffset);
    assert.equal(f.calls[1].destination, f.output.value + profile.monoLayout.rightFingerOffset);
    assert.equal(f.output.readU8(), 1); assert.equal(f.output.add(1).readU8(), 0);
    assert.equal(JSON.stringify(f.api.status().validFrames), '[1,0]');
    assert.equal(JSON.stringify(f.api.status().freshSides), '[true,false]');
    f.advance(500); assert.equal(JSON.stringify(f.api.status().freshSides), '[false,false]');
});
test('hands restore on Update and defer detach and pin free until callback return', () => {
    const f = fixture('hands'); f.api.activate(20); f.dispatch('on_update', f.hmd);
    f.api.deactivate();
    const record = f.modules[0]; record.pendingValue = 1;
    record.symbols.on_update.callback(f.hmd); f.runTimer(0);
    assert.equal(record.phase, 0);
    f.api.deactivate(); assert.equal(record.phase, 0);
    assert.equal(f.hmd.add(64).readU8(), 0); assert.equal(f.shared.add(72).readU8(), 0);
    assert.equal(f.events.some(event => event.startsWith('detach:') || event.startsWith('free:')), false);
    record.pendingValue = 0; f.runTimer(25);
    assert.equal(f.api.status().state, 'stopped'); assert.equal(record.disposed, false);
    assert.equal(f.events.filter(event => event.startsWith('detach:')).length, 2);
    assert.equal(f.events.filter(event => event.startsWith('free:')).length, 2);
    assert.ok(f.events.indexOf('flush') < f.events.indexOf('free:1'));
});
test('Stop during cooperative startup never reopens native work', () => {
    const f = fixture('hands', {onSetter: (sandbox, index) => { if (index === 1) sandbox.rpc.exports.deactivate(); }});
    f.api.activate(20); f.dispatch('on_update', f.hmd);
    assert.equal(f.api.status().state, 'restoring'); assert.equal(f.modules[0].phase, 3);
    assert.equal(f.events.includes('phase:2'), false);
    f.dispatch('on_update', f.hmd); f.runTimer(0);
    assert.equal(f.api.status().state, 'stopped');
    assert.equal(f.hmd.add(64).readU8(), 0); assert.equal(f.shared.add(72).readU8(), 0);
});
test('Stop during conversion prevents subsequent validity writes and conversion', () => {
    const f = fixture('hands', {onConvert: (sandbox, index) => { if (index === 1) sandbox.rpc.exports.deactivate(); }});
    f.api.activate(20); f.dispatch('on_update', f.hmd);
    f.dispatch('on_hand', f.hmd, f.output);
    assert.equal(f.calls.length, 1); assert.equal(f.output.readU8(), 0); assert.equal(f.api.status().frames, 0);
    assert.equal(f.api.status().state, 'restoring');
});
test('conversion exceptions remain contained and request restoration', () => {
    const f = fixture('hands', {convertFailure: true}); f.api.activate(20); f.dispatch('on_update', f.hmd);
    assert.doesNotThrow(() => f.dispatch('on_hand', f.hmd, f.output));
    assert.equal(f.api.status().state, 'restoring'); assert.match(f.api.status().error, /conversion refused/);
    assert.equal(f.modules[0].phase, 3);
});
test('Mono fallback waits for accepted callbacks before restoring', () => {
    const f = fixture('hands'); f.api.activate(20); f.dispatch('on_update', f.hmd); f.api.deactivate();
    f.modules[0].pendingValue = 1; f.runTimer(2500);
    assert.equal(f.invokes.length, 2); assert.equal(f.hmd.add(64).readU8(), 1);
    f.modules[0].pendingValue = 0; f.runTimer(25); f.runTimer(0);
    assert.equal(f.api.status().state, 'stopped'); assert.equal(f.invokes.length, 4);
});
for (const stop of ['explicit', 'lease']) {
    test('fallback and resumed Update restore once after ' + stop + ' Stop', () => {
        let f, overlaps = 0;
        f = fixture('hands', {onSetter: (sandbox, index) => {
            if (index !== 3) return;
            // The first fallback setter releases Frida's JS lock. Model a VD
            // Update arriving before that native call returns, while phase 3
            // still admits an Update restoration callback.
            overlaps++;
            f.dispatch('on_update', f.hmd);
            sandbox.rpc.exports.deactivate(); sandbox.rpc.exports.deactivate();
            assert.equal(f.invokes.length, 3, 'The resumed Update must not invoke another setter');
            assert.equal(f.events.some(event => event.startsWith('detach:') || event.startsWith('free:')), false);
        }});
        f.api.activate(20); f.dispatch('on_update', f.hmd);
        if (stop === 'lease') f.runTimer(20000);
        else f.api.deactivate();
        f.runTimer(2500);
        assert.equal(overlaps, 1); assert.equal(f.api.status().state, 'restoring');
        assert.equal(f.api.status().error, null);
        assert.equal(f.invokes.length, 4, 'Each saved setting is restored once');
        assert.deepEqual(f.invokes.map(call => call.value), [1, 1, 0, 0]);
        assert.equal(f.hmd.add(64).readU8(), 0); assert.equal(f.shared.add(72).readU8(), 0);
        // A late restoration dispatch and repeated Stop cannot restart the
        // transaction or create another cleanup while release is queued.
        f.dispatch('on_update', f.hmd); f.api.deactivate(); f.runTimer(0);
        f.api.deactivate(); f.dispatch('on_update', f.hmd);
        assert.equal(f.api.status().state, 'stopped'); assert.equal(f.api.status().error, null);
        assert.equal(f.invokes.length, 4);
        assert.equal(f.events.filter(event => event.startsWith('detach:')).length, 2);
        assert.equal(f.events.filter(event => event.startsWith('free:')).length, 2);
        assert.equal(f.modules[0].disposed, false);
    });
}
test('a failure after fallback restoration cancels queued successful cleanup', () => {
    // Activation detaches once. The fallback has restored both settings and
    // queued release when its Mono-attached thread fails to detach.
    const f = fixture('hands', {detachFailure: 2});
    f.api.activate(20); f.dispatch('on_update', f.hmd); f.api.deactivate(); f.runTimer(2500);
    assert.equal(f.invokes.length, 4);
    assert.equal(f.api.status().state, 'restore-failed'); assert.match(f.api.status().error, /Mono detach refused/);
    assert.throws(() => f.runTimer(0), /Expected timer/);
    f.api.deactivate(); f.dispatch('on_update', f.hmd);
    assert.equal(f.api.status().state, 'restore-failed');
    assert.equal(f.events.some(event => event.startsWith('free:') || event.startsWith('detach:')), false);
    assert.equal(f.modules[0].disposed, false);
});
for (const failingRelease of [1, 2]) {
    test('failure during pin release ' + failingRelease + ' cannot become a successful Stop', () => {
        const f = fixture('hands', {onPinFree: (sandbox, index) => {
            if (index === failingRelease) {
                // NativeFunction releases the JS lock while freeing a handle.
                // Model another restoration thread reporting its detach error
                // before the active cleanup's native call returns.
                vm.runInContext("restorationFailure(new Error('Concurrent Mono detach refused'));", sandbox);
            }
        }});
        f.api.activate(20); f.dispatch('on_update', f.hmd); f.api.deactivate();
        f.dispatch('on_update', f.hmd); f.runTimer(0);
        assert.equal(f.api.status().state, 'restore-failed');
        assert.match(f.api.status().error, /Concurrent Mono detach refused/);
        assert.equal(f.events.filter(event => event.startsWith('free:')).length, failingRelease);
        assert.equal(vm.runInContext('pins.length', f.sandbox), 2 - failingRelease);
        f.api.deactivate(); f.dispatch('on_update', f.hmd);
        assert.equal(f.api.status().state, 'restore-failed');
        assert.equal(f.modules[0].disposed, false);
    });
}
test('optical fallback drain failure stays sticky and retains pins', () => {
    const f = fixture('hands'); f.api.activate(20); f.dispatch('on_update', f.hmd); f.api.deactivate();
    f.modules[0].pendingValue = 1; f.advance(5000); f.runTimer(2500);
    assert.equal(f.api.status().state, 'restore-failed'); assert.equal(f.modules[0].phase, 0);
    f.api.deactivate(); assert.equal(f.api.status().state, 'restore-failed');
    assert.equal(f.events.some(event => event.startsWith('free:') || event.startsWith('detach:')), false);
    assert.equal(f.modules[0].disposed, false);
});
test('managed restoration exceptions stay visible without freeing pins', () => {
    const f = fixture('hands', {setterFailure: 3}); f.api.activate(20); f.dispatch('on_update', f.hmd);
    f.api.deactivate(); assert.doesNotThrow(() => f.dispatch('on_update', f.hmd));
    assert.equal(f.api.status().state, 'restore-failed'); assert.equal(f.modules[0].phase, 0);
    assert.equal(f.events.some(event => event.startsWith('free:') || event.startsWith('detach:')), false);
});
console.log('Android adapter boundary tests: ' + passed + ' passed.');
