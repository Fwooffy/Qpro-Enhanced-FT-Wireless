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

// Shape independently inspected in the 1.34.25 APK. Keep this fixture explicit:
// a 1,456-byte fixed byte buffer, packed float pose values, and the two aim
// records distinguish the real result from the old four-field assumption.
function inspectedHandsState(facts) {
    facts.types.set(107, {kind: 5});
    facts.types.set(108, {kind: 6});
    facts.types.set(109, {kind: 10});
    facts.types.set(110, {kind: 17, klass: 203}); // fixed byte buffer
    facts.types.set(111, {kind: 17, klass: 204}); // aim
    facts.types.set(112, {kind: 17, klass: 205}); // pose
    facts.types.set(113, {kind: 17, klass: 206}); // quaternion
    facts.types.set(114, {kind: 17, klass: 207}); // vector
    facts.classes.set(200, {size: 3032, align: 8, fields: [300, 301, 320, 302, 303, 321, 322]});
    facts.classes.set(201, {size: 1456, align: 1, fields: [310]});
    facts.classes.set(203, {size: 1456, align: 1, fields: [330]});
    facts.classes.set(204, {size: 56, align: 8, fields: [340, 341, 342, 343, 344, 345]});
    facts.classes.set(205, {size: 28, align: 4, fields: [350, 351]});
    facts.classes.set(206, {size: 16, align: 4, fields: [360, 361, 362, 363]});
    facts.classes.set(207, {size: 12, align: 4, fields: [370, 371, 372]});
    facts.fields.get(300).type = 107; facts.fields.get(301).type = 107;
    facts.fields.get(310).type = 110;
    facts.fields.set(320, {offset: 18, type: 108});
    facts.fields.set(321, {offset: 2936, type: 111});
    facts.fields.set(322, {offset: 2992, type: 111});
    facts.fields.set(330, {offset: 16, type: 107});
    facts.fields.set(340, {offset: 16, type: 109});
    facts.fields.set(341, {offset: 24, type: 112});
    [342, 343, 344, 345].forEach((id, index) => facts.fields.set(id, {offset: 52 + index * 4, type: 104}));
    facts.fields.set(350, {offset: 16, type: 113});
    facts.fields.set(351, {offset: 32, type: 114});
    [360, 361, 362, 363].forEach((id, index) => facts.fields.set(id, {offset: 16 + index * 4, type: 104}));
    [370, 371, 372].forEach((id, index) => facts.fields.set(id, {offset: 16 + index * 4, type: 104}));
}

test('inspected seven-field HandsState proves the unchanged finger prefix and aim tail', () => {
    const value = fixture(inspectedHandsState), proof = value.validate();
    assert.equal(proof.source, 'mono-metadata');
    assert.equal(proof.handBytes, 3032); assert.equal(proof.fingerBytes, 1456);
    assert.deepEqual(Array.from(proof.fingerOffsets), [4, 1460]);
    assert.equal(proof.layout, 'byte-flags-pose-aim');
    assert.deepEqual(value.events, []);
});

test('unfamiliar version with matching reflected layout proceeds', () => {
    const value = fixture(), proof = value.validate();
    assert.equal(proof.source, 'mono-metadata');
    assert.equal(proof.handBytes, 2916); assert.equal(proof.fingerBytes, 1456);
    assert.equal(proof.layout, 'boolean-prefix');
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

test('inspected layout uses offsets rather than declaration order and skips static constants', () => {
    const value = fixture(facts => {
        inspectedHandsState(facts);
        facts.fields.set(390, {offset: 0, type: 105, flags: 16});
        for (const klass of facts.classes.values()) klass.fields.reverse();
        facts.classes.get(200).fields.unshift(390);
        facts.classes.get(204).fields.push(390);
    });
    assert.equal(value.validate().layout, 'byte-flags-pose-aim');
    assert.deepEqual(value.events, []);
});

const invalidInspected = [
    ['short hand buffer', facts => facts.classes.get(200).size = 3024],
    ['extra hand buffer tail', facts => facts.classes.get(200).size = 3040],
    ['wrong hand alignment', facts => facts.classes.get(200).align = 4],
    ['Boolean instead of byte flags', facts => facts.fields.get(300).type = 103],
    ['signed byte instead of unsigned flag', facts => { facts.types.set(115, {kind: 4}); facts.fields.get(300).type = 115; }],
    ['mixed flag encodings', facts => facts.fields.get(301).type = 103],
    ['unsigned instead of signed explicit padding', facts => { facts.types.set(115, {kind: 7}); facts.fields.get(320).type = 115; }],
    ['missing explicit padding', facts => facts.classes.get(200).fields.splice(2, 1)],
    ['padding moved onto a flag', facts => facts.fields.get(320).offset = 17],
    ['finger array with wrong alignment', facts => facts.classes.get(201).align = 4],
    ['fixed buffer with wrong alignment', facts => facts.classes.get(203).align = 4],
    ['fixed buffer with wrong size', facts => facts.classes.get(203).size = 1452],
    ['fixed buffer with float element', facts => facts.fields.get(330).type = 104],
    ['fixed buffer with extra field', facts => { facts.fields.set(390, {offset: 17, type: 107}); facts.classes.get(203).fields.push(390); }],
    ['left aim overlaps the finger prefix', facts => facts.fields.get(321).offset = 2932],
    ['right aim shifted into alignment padding', facts => facts.fields.get(322).offset = 2988],
    ['left and right aim identities differ', facts => {
        facts.classes.set(208, {...facts.classes.get(204)});
        facts.types.set(115, {kind: 17, klass: 208}); facts.fields.get(322).type = 115;
    }],
    ['missing right aim', facts => facts.classes.get(200).fields.pop()],
    ['extra hand field in padding', facts => { facts.fields.set(390, {offset: 2932, type: 107}); facts.classes.get(200).fields.push(390); }],
    ['aim value has wrong size', facts => facts.classes.get(204).size = 52],
    ['aim value has wrong alignment', facts => facts.classes.get(204).align = 4],
    ['aim status has wrong integer width', facts => facts.fields.get(340).type = 108],
    ['aim status is unsigned instead of inspected Int64', facts => { facts.types.set(115, {kind: 11}); facts.fields.get(340).type = 115; }],
    ['aim pose moved', facts => facts.fields.get(341).offset = 28],
    ['aim pinch strengths overlap', facts => facts.fields.get(343).offset = facts.fields.get(342).offset],
    ['aim pinch strength is managed reference', facts => facts.fields.get(342).type = 105],
    ['aim pose is a managed reference', facts => facts.fields.get(341).type = 105],
    ['aim contains by-reference field', facts => facts.types.get(112).byref = true],
    ['aim contains recursive value', facts => facts.fields.get(341).type = 111],
    ['aim contains out-of-bounds field', facts => facts.fields.get(345).offset = 72],
    ['pose value has wrong alignment', facts => facts.classes.get(205).align = 8],
    ['pose rotation and position reversed', facts => {
        facts.fields.get(350).type = 114; facts.fields.get(351).type = 113;
    }],
    ['pose rotation contains a reference', facts => facts.fields.get(360).type = 105],
    ['pose position contains a reference', facts => facts.fields.get(370).type = 105],
    ['rotation value has wrong size', facts => facts.classes.get(206).size = 20],
    ['rotation float components overlap', facts => facts.fields.get(361).offset = 16],
    ['position has an extra field', facts => { facts.fields.set(390, {offset: 16, type: 107}); facts.classes.get(207).fields.push(390); }],
    ['nested boxed/unboxed disagreement', facts => facts.classes.get(207).boxedSize = 12],
];
for (const [name, mutate] of invalidInspected) {
    test('inspected shape rejects ' + name + ' before native work', () => {
        const value = fixture(facts => { inspectedHandsState(facts); mutate(facts); });
        assert.throws(value.validate, /hand-result layout is unsupported/);
        assert.deepEqual(value.events, []);
    });
}

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
