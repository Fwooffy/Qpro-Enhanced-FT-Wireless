'use strict';
// Metadata-only Frida fixtures. No process is attached, no native conversion is
// called, and no headset setting or result buffer is written.
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '..', 'quest_hand_adapter.js'), 'utf8');
let checks = 0;
function test(name, action) {
    try { action(); checks++; }
    catch (error) { error.message = name + ': ' + error.message; throw error; }
}

function fixture(change = () => {}, required = true) {
    const memory = new Map(), calls = [], events = [];
    class Pointer {
        constructor(value) { this.value = value; }
        isNull() { return this.value === 0; }
        equals(other) { return this.value === other.value; }
        toString() { return '0x' + this.value.toString(16); }
        writePointer(other) { memory.set(this.value, other.value); }
        writeU32(value) { memory.set(this.value, value); }
        readU32() { return memory.get(this.value) || 0; }
    }
    const pointer = value => new Pointer(value);
    let next = 10000;
    const facts = {
        types: new Map([
            [100, {kind: 17, byref: true, klass: 200}],
            [101, {kind: 17, byref: true, klass: 201}],
            [102, {kind: 17, klass: 201}],
            [103, {kind: 2}], [104, {kind: 12}], [105, {kind: 18}],
            [106, {kind: 17, klass: 202}],
        ]),
        classes: new Map([
            [200, {size: 2916, align: 4, fields: [300, 301, 302, 303]}],
            [201, {size: 1456, align: 4, fields: [310]}],
            [202, {size: 1456, align: 4, fields: [311]}],
        ]),
        fields: new Map([
            [300, {offset: 16, type: 103}], [301, {offset: 17, type: 103}],
            [302, {offset: 20, type: 102}], [303, {offset: 1476, type: 102}],
            [310, {offset: 16, type: 104}], [311, {offset: 16, type: 104}],
        ]),
        unavailable: new Set(), repeatField: false,
    };
    change(facts);
    const api = {
        mono_get_root_domain: () => pointer(1),
        mono_type_get_type: type => facts.types.get(type.value).kind,
        mono_type_is_byref: type => Number(Boolean(facts.types.get(type.value).byref)),
        mono_type_get_class: type => pointer(facts.types.get(type.value).klass || 0),
        mono_class_is_valuetype: klass => Number(!facts.classes.get(klass.value).reference),
        mono_class_value_size: (klass, alignment) => {
            const value = facts.classes.get(klass.value); alignment.writeU32(value.align); return value.size;
        },
        mono_class_instance_size: klass => {
            const value = facts.classes.get(klass.value); return value.boxedSize ?? value.size + 16;
        },
        mono_class_get_fields: (klass, iterator) => {
            const index = iterator.readU32(); iterator.writeU32(index + 1);
            return pointer(facts.classes.get(klass.value).fields[facts.repeatField ? 0 : index] || 0);
        },
        mono_field_get_flags: field => facts.fields.get(field.value).flags || 0,
        mono_field_get_type: field => pointer(facts.fields.get(field.value).type),
        mono_field_get_offset: field => facts.fields.get(field.value).offset,
    };
    const sandbox = vm.createContext({
        QPRO_PROFILE: {androidVersion: '99.99.99.99', monoLayout: {
            leftFingerOffset: 4, rightFingerOffset: 1460, requireResultMetadata: required,
        }},
        Process: {arch: 'arm64', pointerSize: 8, getModuleByName: () => ({getExportByName(name) {
            if (facts.unavailable.has(name)) throw Error('missing export');
            return {name};
        }})},
        NativeFunction: function(symbol) {
            return (...args) => {
                calls.push(symbol.name);
                return api[symbol.name] ? api[symbol.name](...args) : pointer(0);
            };
        },
        Memory: {alloc(size) { const block = pointer(next); next += size + 16; return block; }},
        ptr: pointer, rpc: {exports: {}}, Script: {runtime: 'QJS'},
        NativeCallback: function() { events.push('native-callback'); },
        CModule: function() { events.push('native-compile'); },
        Interceptor: {attach() { events.push('attach'); }, flush() {}},
        handType: pointer(100), fingerType: pointer(101),
    });
    vm.runInContext(source, sandbox);
    return {
        facts, calls, events,
        validate: () => vm.runInContext('validateHandResultLayout(handType, fingerType)', sandbox),
    };
}

test('unfamiliar version with matching reflected layout proceeds', () => {
    const value = fixture(), proof = value.validate();
    assert.equal(proof.source, 'mono-metadata');
    assert.equal(proof.handBytes, 2916); assert.equal(proof.fingerBytes, 1456);
    assert.deepEqual(Array.from(proof.fingerOffsets), [4, 1460]);
    assert.deepEqual(value.events, []);
});
test('known researched profile does not require new Mono exports', () => {
    const value = fixture(facts => facts.unavailable.add('mono_class_value_size'), false);
    assert.equal(value.validate().source, 'profile');
    assert.equal(value.calls.includes('mono_class_value_size'), false);
});
test('static constants do not alter instance layout', () => {
    const value = fixture(facts => {
        facts.fields.set(320, {offset: 0, type: 105, flags: 16});
        facts.classes.get(200).fields.push(320);
    });
    assert.equal(value.validate().source, 'mono-metadata');
});

const invalid = [
    ['missing Mono metadata', facts => facts.unavailable.add('mono_class_get_fields')],
    ['managed-reference hand parameter', facts => facts.types.get(100).kind = 18],
    ['non-byref conversion parameter', facts => facts.types.get(101).byref = false],
    ['shifted right finger result', facts => facts.fields.get(303).offset += 4],
    ['ambiguous duplicate validity field', facts => facts.fields.get(301).offset = 16],
    ['missing result field', facts => facts.classes.get(200).fields.pop()],
    ['extra result field', facts => { facts.fields.set(320, {offset: 18, type: 103}); facts.classes.get(200).fields.push(320); }],
    ['wrong converter field class', facts => facts.fields.get(303).type = 106],
    ['non-Boolean validity byte', facts => facts.fields.get(300).type = 104],
    ['finger span mismatch', facts => facts.classes.get(201).size = 1452],
    ['boxed/unboxed disagreement', facts => facts.classes.get(201).boxedSize = 1456],
    ['unsupported alignment', facts => facts.classes.get(201).align = 3],
    ['oversized value type', facts => facts.classes.get(201).size = 1 << 20],
    ['managed reference inside finger result', facts => facts.fields.get(310).type = 105],
    ['recursive finger value', facts => facts.fields.get(310).type = 102],
    ['unbounded repeated field', facts => facts.repeatField = true],
    ['overlapping finger fields', facts => {
        facts.fields.set(321, {offset: 18, type: 104}); facts.classes.get(201).fields.push(321);
    }],
    ['field outside finger result', facts => facts.fields.get(310).offset = 16 + 1456],
];
for (const [name, mutate] of invalid) {
    test(name + ' refuses before native work', () => {
        const value = fixture(mutate);
        assert.throws(value.validate, /hand-result layout is unsupported/);
        assert.deepEqual(value.events, []);
    });
}
console.log('PASS: ' + checks + ' hand-result metadata checks; no live Frida, native hooks, or headset changes.');
