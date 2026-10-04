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
    let nextMemory = 40000, nextTimer = 0, clock = 1000, nextPin = 0;
    const calls = [], invokes = [];
    const monoFunctions = {
        mono_get_root_domain: () => address(9000),
        mono_thread_attach: () => { events.push('mono-attach'); return address(9100); },
        mono_thread_detach: () => events.push('mono-detach'),
        mono_gchandle_new: object => { events.push('pin:' + object.value); return ++nextPin; },
        mono_gchandle_free: handle => events.push('free:' + handle),
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
            return pointer.call;
        },
        NativeCallback: function(callback, result, args) { return {callback, result, args}; },
        Memory: {alloc: size => { const block = address(nextMemory); nextMemory += size + 16; return block; }},
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
            update: address(1000), handState: address(2000), shared,
            hmdOffset: 64, sharedOffset: 72, hmdSetter: address(70), sharedSetter: address(80),
            convert: (owner, side, destination) => {
                calls.push({owner: owner.value, side, destination: destination.value});
                if (options.onConvert) options.onConvert(sandbox, calls.length);
                if (options.convertFailure) throw Error('conversion refused');
                return options.validSides ? Number(options.validSides[side]) : 1;
            }
        };
        vm.runInContext('bindings = testBindings;', sandbox);
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
        runTimer, dispatch, advance: milliseconds => { clock += milliseconds; }};
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
