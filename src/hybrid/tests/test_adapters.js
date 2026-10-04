'use strict';
// Source syntax and selected lease/resolver guards are checked in an isolated
// VM. No real Frida APIs, OS processes or hardware are accessed.
const fs = require('fs');
const vm = require('vm');
const path = require('path');
const assert = require('assert');
const directory = path.resolve(__dirname, '..');
const profile = JSON.parse(fs.readFileSync(path.join(directory, 'compatibility.json')));
for (const name of ['quest_hand_adapter.js', 'quest_controller_adapter.js', 'steamvr_skeleton_adapter.js']) {
    new vm.Script(fs.readFileSync(path.join(directory, name), 'utf8'), {filename: name});
}
let attachments = 0, timers = [], detached = 0;
const context = vm.createContext({
    QPRO_PROFILE: profile, rpc: {exports: {}},
    Process: {arch: 'arm64', getModuleByName: () => ({enumerateRanges: () => []})},
    Interceptor: {attach: () => {attachments++; return {detach() {detached++;}};}},
    setTimeout: callback => {timers.push(callback); return timers.length;}, clearTimeout() {},
    ptr: value => value
});
vm.runInContext(fs.readFileSync(path.join(directory, 'quest_controller_adapter.js'), 'utf8'), context);
assert.throws(() => context.rpc.exports.validate(), /ambiguous or unsupported/);
assert.equal(attachments, 0);
assert.throws(() => context.rpc.exports.heartbeat(0), /Invalid controller lease/);
console.log('Hybrid adapters: syntax, unsupported resolver and no preflight hook passed.');

const memoryReads = {float: 0, byteArray: 0, byteArraySize: 0, module: 0, range: 0};
class Address {
    constructor(value, memory = new Map()) { this.value = Number(value); this.memory = memory; }
    add(amount) { return new Address(this.value + amount, this.memory); }
    sub(amount) { return new Address(this.value - Number(amount instanceof Address ? amount.value : amount), this.memory); }
    compare(other) { return Math.sign(this.value - other.value); }
    equals(other) { return this.value === Number(other instanceof Address ? other.value : other); }
    isNull() { return this.value === 0; }
    toUInt32() { return this.value >>> 0; }
    toInt32() { return this.value | 0; }
    toString() { return '0x' + this.value.toString(16); }
    readPointer() { return new Address(this.memory.get(this.value) || 0, this.memory); }
    writePointer(value) { this.memory.set(this.value, value.value); }
    readU8() { return Number(this.memory.get(this.value) || 0); }
    readU32() { return Number(this.memory.get(this.value) || 0); }
    readFloat() { memoryReads.float++; return this.memory.has(this.value) ? Number(this.memory.get(this.value)) : 0; }
    readByteArray(size) {
        memoryReads.byteArray++; memoryReads.byteArraySize = size;
        const buffer = new ArrayBuffer(size), view = new DataView(buffer);
        for (let offset = 0; offset < size; offset += 4) view.setFloat32(offset,
            this.memory.has(this.value + offset) ? Number(this.memory.get(this.value + offset)) : 0, true);
        return buffer;
    }
    readU64() { return new Handle(this.memory.get(this.value) || 0); }
    writeU8(value) { this.memory.set(this.value, value); }
    writeS32(value) { this.memory.set(this.value, value); }
    writeU32(value) { this.memory.set(this.value, value); }
    writeU64(value) { this.memory.set(this.value, value instanceof Handle ? value.value : value); }
}
class Handle {
    constructor(value) { this.value = BigInt(value); }
    equals(other) { return this.value === BigInt(other instanceof Handle ? other.value : other); }
    toNumber() { return Number(this.value); }
}

function androidNativeMock(source, symbols) {
    assert(source.includes('GumInvocationContext'));
    assert(symbols.shared instanceof Address);
    return {
        state_size: () => 64, invocation_size: () => 32, snapshot_size: () => 32, id_size: () => 16,
        initialize_state: () => 1, start() {}, disable() {}, pending: () => 0,
        snapshot(out) {
            out.writeU64(0); out.add(8).writeU64(0); out.add(16).writeU32(0);
            out.add(20).writeU32(0); out.add(24).writeU32(0); return 32;
        }, onEnter: new Address(9000), onLeave: new Address(9001), dispose() {}
    };
}

// Positive native resolution exercises the actual disassembly/data-flow code,
// then a changed side-flag branch must be refused without attaching anything.
function nativeFixture(wrongSide = false, options = {}) {
    const rows = new Map();
    const flags = options.flags || 'w8', prefix = [], expression = [];
    if (options.call) prefix.push({mnemonic: options.call, opStr: options.call === 'bl' ? '#0x1800' : 'x22', regsAccessed: {written: ['x30']}});
    if (options.tstMetadataAlias) prefix.push({mnemonic: 'tst', opStr: flags + ', #4', regsAccessed: {written: ['nzcv', flags]}});
    if (options.clobber === 'flags') prefix.push({mnemonic: 'mov', opStr: 'x' + flags.slice(1) + ', #7', regsAccessed: {written: ['x' + flags.slice(1)]}});
    if (options.clobber === 'sp') prefix.push({mnemonic: 'add', opStr: 'sp, sp, #0x10', regsAccessed: {written: ['sp']}});
    if (options.clobber === 'wsp') prefix.push({mnemonic: 'add', opStr: 'wsp, wsp, #0x10', regsAccessed: {written: ['wsp']}});
    if (options.missingMetadata) prefix.push({mnemonic: 'nop', opStr: ''});
    if (options.expressionClobber === 'side-byte') expression.push({mnemonic: 'ldrb', opStr: flags + ', [sp, #0x24]', regsAccessed: {written: [flags]}});
    if (options.expressionClobber === 'held-byte') expression.push({mnemonic: 'ldrb', opStr: 'w11, [sp, #0x24]', regsAccessed: {written: ['w11']}});
    if (options.expressionClobber === 'side-pointer') expression.push({mnemonic: 'ldr', opStr: 'x' + flags.slice(1) + ', [sp, #0x28]', regsAccessed: {written: ['x' + flags.slice(1)]}});
    const row = (address, mnemonic, opStr) => rows.set(address >= 556 && address < 4096 ?
        address + prefix.length * 4 + (address >= 560 ? expression.length * 4 : 0) : address,
        {mnemonic, opStr, regsAccessed: {written: []}});
    row(492, 'add', 'x2, sp, #0x20'); row(496, 'ldr', flags + ', [sp, #0x40]');
    row(500, 'ldr', 'w1, [sp, #0x30]'); row(504, 'mov', 'x0, x19');
    row(508, 'strb', 'wzr, [sp, #0x20]'); row(512, 'bl', '#0x1000');
    row(516, 'tbz', flags + ', #2, #0x220'); row(520, 'ldrb', 'w9, [sp, #0x20]');
    row(524, 'str', 'w9, [sp, #0x50]'); row(528, 'b', '#0x22c');
    row(544, 'tbz', flags + ', #' + (wrongSide ? 4 : 3) + ', #0x22c');
    row(548, 'ldrb', 'w10, [sp, #0x20]'); row(552, 'str', 'w10, [sp, #0x54]');
    row(556, 'ldrb', 'w11, [sp, #0x20]');
    for (let index = 0; index < 4; index++) row(560 + index * 4, 'mov', 'x' + (12 + index) + ', #0x' + ((index + 1) * 256).toString(16));
    row(576, 'tst', 'w11, #1'); row(580, 'csel', 'x16, x12, x13, ne');
    row(584, 'csel', 'x17, x14, x15, ne'); row(588, 'tst', flags + ', #4');
    row(592, 'csel', 'x18, x16, x17, ne'); row(596, 'add', 'x0, x25, x18');
    const functionRows = [
        ['mov', 'x19, x0'], ['mov', 'w20, w1'], ['mov', 'x21, x2'], ['nop', ''],
        ['ldr', 'x0, [x19, #8]'], ['cbz', 'x0, #0x1030'], ['ldr', 'x22, [x0]'],
        ['ldr', 'x23, [x22, #16]'], ['mov', 'w1, w20'], ['mov', 'x2, x21'],
        ['blr', 'x23'], ['mov', 'w24, w0'], ['mov', 'w0, w24'], ['ret', '']
    ];
    functionRows.forEach(([mnemonic, operands], index) => row(4096 + index * 4, mnemonic, operands));
    prefix.forEach((instruction, index) => rows.set(556 + index * 4, instruction));
    expression.forEach((instruction, index) => rows.set(560 + prefix.length * 4 + index * 4, instruction));
    if (options.expressionMissingMetadata === 'start') delete rows.get(556 + prefix.length * 4).regsAccessed;
    if (options.expressionMissingMetadata === 'interior') delete rows.get(560 + (prefix.length + expression.length) * 4).regsAccessed;
    let hooks = 0;
    const sandbox = vm.createContext({
        QPRO_PROFILE: profile, rpc: {exports: {}}, ptr: value => new Address(Number(value)),
        Process: {arch: 'arm64', pointerSize: 8, getModuleByName: () => ({enumerateRanges: () => [{base: new Address(0), size: 65536}]})},
        Instruction: {parse: address => {if (!rows.has(address.value)) throw Error('Unknown synthetic instruction'); return rows.get(address.value);}},
        Memory: {scanSync: () => [{address: new Address(512)}], alloc: () => new Address(8000, new Map())},
        CModule: androidNativeMock, NativeFunction: function(fn) {return fn;}, Script: {runtime: 'QJS'},
        Interceptor: {attach: () => {hooks++; return {detach() {}};}, flush() {}}, setTimeout: () => 1, clearTimeout() {}
    });
    vm.runInContext(fs.readFileSync(path.join(directory, 'quest_controller_adapter.js'), 'utf8'), sandbox);
    return {sandbox, hookCount: () => hooks};
}
const validNative = nativeFixture();
assert.equal(validNative.sandbox.rpc.exports.validate().compatible, true);
assert.equal(validNative.hookCount(), 0);
validNative.sandbox.rpc.exports.activate(20);
assert.equal(validNative.hookCount(), 1);
validNative.sandbox.rpc.exports.deactivate();
const wrongNative = nativeFixture(true);
assert.throws(() => wrongNative.sandbox.rpc.exports.validate(), /ambiguous or unsupported/);
assert.equal(wrongNative.hookCount(), 0);

// Returning calls preserve AAPCS64 saved registers; caller-saved flags cannot
// prove the later routing expression still uses the query caller's side flag.
for (const call of ['bl', 'blr']) {
    const savedFlags = nativeFixture(false, {call, flags: 'w23'});
    assert.equal(savedFlags.sandbox.rpc.exports.validate().compatible, true);
    assert.equal(savedFlags.hookCount(), 0);
    // Capstone can list a TST source register as written for this alias; TST
    // changes NZCV only and must not erase a saved side flag's provenance.
    const readOnlyAlias = nativeFixture(false, {call, flags: 'w23', tstMetadataAlias: true});
    assert.equal(readOnlyAlias.sandbox.rpc.exports.validate().compatible, true);
    assert.equal(readOnlyAlias.hookCount(), 0);
    const volatileFlags = nativeFixture(false, {call, flags: 'w8'});
    assert.throws(() => volatileFlags.sandbox.rpc.exports.validate(), /ambiguous or unsupported/);
    assert.equal(volatileFlags.hookCount(), 0);
    for (const protection of [{clobber: 'flags'}, {clobber: 'sp'}, {clobber: 'wsp'}, {missingMetadata: true}]) {
        const unprovenRoute = nativeFixture(false, {call, flags: 'w23', ...protection});
        assert.throws(() => unprovenRoute.sandbox.rpc.exports.validate(), /ambiguous or unsupported/);
        assert.equal(unprovenRoute.hookCount(), 0);
    }
}

// Every expression instruction must retain proven side/held inputs. Loading a
// different stack slot destroys that register's earlier byte provenance.
for (const protection of [
    {expressionClobber: 'side-byte'}, {expressionClobber: 'held-byte'},
    {expressionClobber: 'side-pointer'}, {expressionMissingMetadata: 'start'},
    {expressionMissingMetadata: 'interior'}
]) {
    const unprovenExpression = nativeFixture(false, {flags: 'w23', ...protection});
    assert.throws(() => unprovenExpression.sandbox.rpc.exports.validate(), /ambiguous or unsupported/);
    assert.equal(unprovenExpression.hookCount(), 0);
}

// The native C callbacks are exercised in the owned-child Frida harness. This
// VM verifies integration, ABI refusal, route ownership, native freshness and
// asynchronous cleanup without duplicating the C implementation in JavaScript.
function pcFixture(options = {}) {
    const bytes = new Map(), address = value => new Address(value, bytes), base = 1000000;
    const layout = profile.pcLayout;
    const write = (pointer, offset, value) => bytes.set(pointer + offset, value);
    write(base, layout.driverContext, 20000); write(20000, 0, 21000); write(21000, 0, 31000);
    write(22000, 0, 23000); write(23000, 48, 32000);
    write(base, layout.driverHost, 24000); write(24000, 0, 25000); write(25000, 8, 33000);
    write(base, layout.skeletonOriginal, options.savedAddress ?? 36000);
    write(base, layout.controllerTable, 26000); write(base, layout.skeletonTable, 100n);
    write(26000, layout.handPointer, 27000); write(26000, layout.role, 1); write(27000, layout.role, 1);
    write(26000, layout.deviceIndex, 2); write(27000, layout.deviceIndex, 3);
    write(26000, layout.skeleton, 100n); write(27000, layout.skeleton, 200n);
    write(26000, layout.dataPointer, 28000); write(28000, 0, 29000);
    write(28000, layout.opticalFlags, 1); write(29000, layout.frameFlags, 1);
    const stats = new Array(45).fill(0); stats[43] = stats[44] = 0xffffffff;
    const observed = {attachments: [], detached: 0, flushed: 0, compiled: 0, disposed: 0, enabled: false,
        sideStates: [], tick: null, immediate: [], timers: [], modulePresent: true, now: 1000};
    let allocation = 4000000;
    const functions = new Map(), driver = {base: address(base), size: options.driverSize ?? 1000000};
    function exportFunction(fn) {const pointer = address(allocation++); functions.set(pointer.value, fn); return pointer;}
    const sandbox = vm.createContext({
        QPRO_PROFILE: options.profile || profile, rpc: {exports: {}}, ptr: address, uint64: value => new Handle(value),
        Date: {now: () => observed.now},
        Process: {arch: options.arch || 'x64', pointerSize: 8,
            getModuleByName: name => name === 'kernel32.dll' ? {getExportByName: () => address(37000)} : driver,
            findModuleByName: () => {memoryReads.module++; return observed.modulePresent ? driver : null;},
            findRangeByAddress: pointer => {
                memoryReads.range++;
                if (options.unreadable === pointer.value) return null;
                return {base: address(0), size: 8000000, protection: options.nonExecutable === pointer.value ? 'rw-' : 'rwx'};
            },
            attachModuleObserver: callbacks => {observed.moduleObserver = callbacks; return {detach() {}};}},
        Memory: {alloc: size => {const value = address(allocation); allocation += size; return value;},
            allocUtf8String: text => text},
        CModule: options.unavailable ? undefined : function(source, symbols, compiler) {
            observed.compiled++;
            assert.equal(compiler.toolchain, 'internal');
            assert(symbols.qpro_state instanceof Address); assert.equal(symbols.qpro_now.value, 37000);
            if (options.compileFailure) throw Error('Synthetic compile refusal');
            const module = {
                qpro_state_size: exportFunction(() => options.stateSize ?? 512),
                qpro_abi: exportFunction(out => (options.abi || [280, 276, 279, 64, 8, 8]).forEach((v, i) => out.add(i * 4).writeU32(v))),
                qpro_initialize: exportFunction(() => {
                    if (options.initializeFailure) throw Error('Synthetic initialization refusal');
                    stats.fill(0); stats[43] = stats[44] = 0xffffffff;
                }),
                qpro_set_side: exportFunction((side, controller, hand, handle, device, physical, active) =>
                    {observed.sideStates[side] = {controller, hand, handle, device, physical, active};}),
                qpro_enable: exportFunction(value => {observed.enabled = !!value;}),
                qpro_module_alive: exportFunction(value => {if (!value) observed.enabled = false;}),
                qpro_snapshot: exportFunction(out => stats.forEach((v, i) => out.add(i * 4).writeU32(v))),
                dispose() {observed.disposed++;}
            };
            for (const name of ['qpro_skeleton_enter', 'qpro_skeleton_leave', 'qpro_pose_enter', 'qpro_pose_leave'])
                module[name] = exportFunction(() => {});
            observed.module = module;
            return module;
        },
        NativeFunction: function(pointer, _result, _args, settings) {
            if (pointer.value === 31000) return () => address(22000);
            assert.equal(settings.scheduling, 'cooperative'); assert.equal(settings.traps, 'none');
            assert(functions.has(pointer.value), 'A native export must be resolved explicitly');
            return functions.get(pointer.value);
        },
        Interceptor: {attach(pointer, callbacks) {
            assert(callbacks.onEnter instanceof Address); assert(callbacks.onLeave instanceof Address);
            observed.attachments.push({pointer, callbacks});
            return {detach() {observed.detached++;}};
        }, flush() {observed.flushed++;}},
        setImmediate: fn => {observed.immediate.push(fn); return observed.immediate.length;},
        setTimeout: fn => {observed.timers.push(fn); return observed.timers.length;}, clearTimeout() {},
        setInterval: fn => {observed.tick = fn; return 1;}, clearInterval() {}
    });
    vm.runInContext(fs.readFileSync(path.join(directory, 'steamvr_skeleton_adapter.js'), 'utf8'), sandbox);
    return {api: sandbox.rpc.exports, sandbox, observed, bytes, stats, write, layout, driver};
}
const pc = pcFixture();
assert.equal(pc.api.validate().callbackBackend, 'native');
assert.equal(pc.observed.attachments.length, 0, 'Compilation and layout checks happen before hooks');
pc.api.activate(20);
assert.equal(pc.observed.enabled, true);
assert.deepEqual(pc.observed.attachments.map(item => item.pointer.value), [36000, 33000],
    'Only saved optical skeleton and pose implementations are observed');
assert.equal(pc.bytes.get(27000 + pc.layout.skeleton), 100n);
assert.equal(pc.observed.sideStates[0].active, 1);
assert.equal(pc.api.status().activeSides[0], false);
pc.stats[6] = 1; pc.stats[43] = 20; pc.observed.tick();
assert.equal(pc.api.status().activeSides[0], true);
assert.equal(pc.api.status().skeletonSubmissions[0], 1);
pc.stats[43] = 500;
assert.equal(pc.api.status().activeSides[0], false, 'RPC status also rejects stale submissions without a route poll');
pc.stats[43] = 20; pc.stats[8] = 2;
assert.equal(pc.api.status().activeSides[0], false, 'A native fault cannot report a side ready before the route poll');
pc.stats[8] = 0;
// Newly observed counters cannot make old native submissions fresh after a
// JavaScript pause. Age is measured on the native monotonic clock.
pc.stats[6] = 2; pc.stats[43] = 700; pc.observed.tick();
assert.equal(pc.api.status().activeSides[0], false);
pc.write(28000, pc.layout.opticalFlags, 0); pc.observed.tick();
assert.equal(pc.observed.sideStates[0].active, 0);
assert.equal(pc.api.status().routes[0].reason, 'optical-hand-unavailable');
pc.write(28000, pc.layout.opticalFlags, 1); pc.observed.tick();
pc.write(28000, 0, 0); pc.observed.tick();
assert.equal(pc.bytes.get(27000 + pc.layout.skeleton), 200n);
assert.equal(pc.observed.sideStates[0].active, 0);
assert.equal(pc.api.status().routes[0].reason, 'tracking-frame-unavailable');
pc.write(28000, 0, 29000); pc.observed.tick();
// Disable is immediate, but detaching while a redirected original call is
// pending could skip its leave callback and invalidate/free its pose.
pc.stats[9] = 1; pc.api.deactivate();
assert.equal(pc.observed.enabled, false); assert.equal(pc.observed.detached, 0);
assert.equal(pc.api.status().state, 'restoring');
pc.stats[9] = 0; pc.observed.timers.at(-1)();
assert.equal(pc.api.status().state, 'stopped'); assert.equal(pc.observed.detached, 2);
assert.equal(pc.observed.flushed, 1);
assert.equal(pc.bytes.get(27000 + pc.layout.skeleton), 200n);
assert.equal(pc.bytes.get(26000 + pc.layout.multiModal), 0);
assert.equal(pc.observed.disposed, 0, 'An attached CModule stays retained until native script-unload drain');

const stalled = pcFixture(); stalled.api.activate(20);
stalled.stats[9] = 1; stalled.api.deactivate(); stalled.observed.now += 2001;
stalled.observed.timers.at(-1)();
assert.equal(stalled.api.status().state, 'restore-failed');
assert.equal(stalled.observed.detached, 0); assert.equal(stalled.observed.disposed, 0);
assert.equal(stalled.observed.enabled, false);
const failed = pcFixture(); failed.api.activate(20); failed.stats[8] = 2; failed.observed.tick();
assert.equal(failed.api.status().state, 'failing');
assert.equal(failed.observed.enabled, false); assert.equal(failed.observed.detached, 0);
failed.observed.immediate.shift()();
assert.equal(failed.api.status().state, 'stopped'); assert.equal(failed.observed.detached, 2);
const unloaded = pcFixture(); unloaded.api.activate(20);
unloaded.observed.modulePresent = false;
unloaded.observed.moduleObserver.onRemoved(unloaded.driver);
assert.equal(unloaded.observed.enabled, false); assert.equal(unloaded.api.status().state, 'failing');
unloaded.observed.immediate.shift()();
assert.equal(unloaded.api.status().state, 'stopped');

// Missing native backend or wrong ABI has no fallback onto driver-thread
// JavaScript. Stored callable validation still precedes native preparation.
for (const options of [
    {unavailable: true}, {arch: 'arm64'}, {compileFailure: true}, {initializeFailure: true},
    {stateSize: 4097}, {abi: [280, 275, 279, 64, 8, 8]}, {abi: [280, 276, 279, 1025, 8, 8]}
]) {
    const refusal = pcFixture(options);
    assert.throws(() => refusal.api.validate(), /Native SteamVR|native monotonic/);
    assert.equal(refusal.observed.attachments.length, 0);
    assert.equal(refusal.bytes.get(27000 + refusal.layout.skeleton), 200n);
    if (options.initializeFailure) {
        assert.equal(refusal.observed.disposed, 1);
        assert.equal(refusal.api.status().nativeFault, 0, 'A failed initializer cannot publish disposed native functions');
    }
}
for (const [options, message] of [
    [{savedAddress: 0}, /saved skeleton callable is not executable/],
    [{nonExecutable: 36000}, /saved skeleton callable is not executable/],
    [{unreadable: 1000000 + profile.pcLayout.skeletonOriginal}, /Saved skeleton callable slot is not readable/],
    [{driverSize: profile.pcLayout.skeletonOriginal + 8}, /VD driver is too small/],
    [{profile: {...profile, pcLayout: {...profile.pcLayout, skeletonOriginal: undefined}}}, /saved skeleton callable is missing/]
]) {
    const refusal = pcFixture(options);
    assert.throws(() => refusal.api.validate(), message);
    assert.equal(refusal.observed.attachments.length, 0); assert.equal(refusal.observed.compiled, 0);
}
for (const savedAddress of [32000, 36000]) {
    const deduplicated = pcFixture({savedAddress});
    assert.equal(deduplicated.api.validate().skeletonObservers, 1);
    assert.equal(deduplicated.observed.attachments.length, 0);
}
const initialRouteFailure = pcFixture();
initialRouteFailure.api.validate();
initialRouteFailure.write(28000, initialRouteFailure.layout.opticalFlags, 2);
assert.throws(() => initialRouteFailure.api.activate(20), /optical validity/);
assert.equal(initialRouteFailure.observed.enabled, false, 'Initial route failure cannot re-enable native callbacks');
console.log('Hybrid adapters: native preflight, monotonic freshness, route loss, deferred draining, retention and restoration passed.');
