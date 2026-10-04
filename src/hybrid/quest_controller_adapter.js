'use strict';
// Independently validate the ARM64 ABI and the caller's two-sided routing.
// No firmware address or unvalidated substring match is used as a target.
if (Process.arch !== 'arm64') throw new Error('Hands require an ARM64 runtime');
const library = Process.getModuleByName('libvrapiimpl.so');
const executable = library.enumerateRanges('r-x');
let state = 'idle', error = null, hook = null, timer = null, resolution = null;
let native = null, cleanupTimer = null, cleanupDeadline = 0;
let queries = 0, changes = 0;
let ids = {};
// Writable state is supplied externally: CModule's own data is read-only.
const QUERY_CALLBACKS = `
#include <gum/guminterceptor.h>
#include <gum/gummemory.h>
#include <glib.h>
typedef struct {
    GMutex lock;
    gpointer caller;
    GHashTable * ids;
    guint enabled, pending, error;
    guint64 queries, changes;
} QueryState;
typedef struct { gpointer output; guint id, applies, counted; } QueryInvocation;
typedef struct { guint64 queries, changes; guint error, pending, count, padding; } QuerySnapshot;
typedef struct { guint id, padding; guint64 count; } IdSnapshot;
extern QueryState shared;
extern guint initialized;
guint state_size(void) { return sizeof(QueryState); }
guint invocation_size(void) { return sizeof(QueryInvocation); }
guint snapshot_size(void) { return sizeof(QuerySnapshot); }
guint id_size(void) { return sizeof(IdSnapshot); }
gboolean initialize_state(guint capacity) {
    if (capacity < sizeof(QueryState)) return FALSE;
    shared.lock.p = NULL; g_mutex_init(&shared.lock);
    shared.caller = NULL; shared.enabled = shared.pending = shared.error = 0;
    shared.queries = shared.changes = 0;
    shared.ids = g_hash_table_new_full(g_direct_hash, g_direct_equal, NULL, g_free);
    initialized = 1; return TRUE;
}
void start(gpointer caller) {
    g_mutex_lock(&shared.lock);
    shared.caller = caller; shared.enabled = 1;
    g_mutex_unlock(&shared.lock);
}
void disable(void) {
    g_mutex_lock(&shared.lock); shared.enabled = 0; g_mutex_unlock(&shared.lock);
}
guint pending(void) {
    guint value;
    g_mutex_lock(&shared.lock); value = shared.pending; g_mutex_unlock(&shared.lock);
    return value;
}
void onEnter(GumInvocationContext * ic) {
    QueryInvocation * call = GUM_IC_GET_INVOCATION_DATA(ic, QueryInvocation);
    call->counted = call->applies = 0; call->output = NULL; call->id = 0;
    g_mutex_lock(&shared.lock);
    if (shared.enabled) {
        shared.pending++; call->counted = 1;
        call->applies = gum_invocation_context_get_return_address(ic) == shared.caller;
        call->output = gum_invocation_context_get_nth_argument(ic, 2);
        call->id = GPOINTER_TO_UINT(gum_invocation_context_get_nth_argument(ic, 1));
    }
    g_mutex_unlock(&shared.lock);
}
void onLeave(GumInvocationContext * ic) {
    QueryInvocation * call = GUM_IC_GET_INVOCATION_DATA(ic, QueryInvocation);
    guint8 * bytes;
    gsize length = 0;
    guint64 * count;
    if (!call->counted) return;
    g_mutex_lock(&shared.lock);
    if (shared.enabled && call->applies &&
            GPOINTER_TO_INT(gum_invocation_context_get_return_value(ic)) == 0) {
        bytes = gum_memory_read(call->output, 1, &length);
        if (bytes == NULL || length != 1) { shared.error = 1; shared.enabled = 0; }
        else if (bytes[0] > 1) { shared.error = 2; shared.enabled = 0; }
        else {
            shared.queries++;
            count = g_hash_table_lookup(shared.ids, GUINT_TO_POINTER(call->id));
            if (count == NULL) {
                count = g_new0(guint64, 1);
                g_hash_table_insert(shared.ids, GUINT_TO_POINTER(call->id), count);
            }
            (*count)++;
            if (bytes[0] == 0) {
                bytes[0] = 1;
                if (gum_memory_write(call->output, bytes, 1)) shared.changes++;
                else { shared.error = 1; shared.enabled = 0; }
            }
        }
        g_free(bytes);
    }
    shared.pending--; call->counted = 0;
    g_mutex_unlock(&shared.lock);
}
guint snapshot(gpointer output, guint capacity) {
    guint count, needed;
    QuerySnapshot * header = output;
    IdSnapshot * rows;
    GHashTableIter iter;
    gpointer key, value;
    g_mutex_lock(&shared.lock);
    count = g_hash_table_size(shared.ids);
    if (count > (1048576 - sizeof(QuerySnapshot)) / sizeof(IdSnapshot)) {
        shared.error = 3; shared.enabled = 0;
        g_mutex_unlock(&shared.lock); return 0;
    }
    needed = sizeof(QuerySnapshot) + count * sizeof(IdSnapshot);
    if (capacity >= needed) {
        header->queries = shared.queries; header->changes = shared.changes;
        header->error = shared.error; header->pending = shared.pending;
        header->count = count; header->padding = 0;
        rows = (IdSnapshot *) (header + 1);
        g_hash_table_iter_init(&iter, shared.ids);
        while (g_hash_table_iter_next(&iter, &key, &value)) {
            rows->id = GPOINTER_TO_UINT(key); rows->padding = 0;
            rows->count = *((guint64 *) value); rows++;
        }
    }
    g_mutex_unlock(&shared.lock); return needed;
}
void finalize(void) {
    if (!initialized) return;
    g_hash_table_unref(shared.ids); g_mutex_clear(&shared.lock); initialized = 0;
}
`;
function prepareNative() {
    if (native !== null) return native;
    const storage = Memory.alloc(256), initialized = Memory.alloc(4);
    initialized.writeU32(0);
    const module = new CModule(QUERY_CALLBACKS, {shared: storage, initialized}, {toolchain: 'internal'});
    const bind = (name, result, args = []) => new NativeFunction(module[name], result, args,
        {scheduling: 'cooperative', traps: 'none'});
    try {
        const size = bind('state_size', 'uint')(), invocationSize = bind('invocation_size', 'uint')();
        if (Process.pointerSize !== 8 || size < 1 || size > 256 || invocationSize < 1 || invocationSize > 1024 ||
                bind('snapshot_size', 'uint')() !== 32 || bind('id_size', 'uint')() !== 16)
            throw new Error('Native controller callback layout is unsupported');
        if (!bind('initialize_state', 'int', ['uint'])(256)) throw new Error('Native controller callbacks did not initialize');
        native = {module, storage, initialized, start: bind('start', 'void', ['pointer']),
            disable: bind('disable', 'void'), pending: bind('pending', 'uint'),
            snapshot: bind('snapshot', 'uint', ['pointer', 'uint']), buffer: Memory.alloc(1024), capacity: 1024,
            attached: false};
        return native;
    } catch (failure) { module.dispose(); throw failure; }
}
function readCounters() {
    if (native === null) return;
    let bytes = native.snapshot(native.buffer, native.capacity);
    if (bytes > native.capacity && bytes <= 1048576) {
        native.buffer = Memory.alloc(bytes); native.capacity = bytes;
        bytes = native.snapshot(native.buffer, native.capacity);
    }
    if (bytes < 32 || bytes > native.capacity) throw new Error('Native controller counter snapshot is unsupported');
    queries = native.buffer.readU64().toNumber(); changes = native.buffer.add(8).readU64().toNumber();
    const count = native.buffer.add(24).readU32(), failure = native.buffer.add(16).readU32();
    if (32 + count * 16 !== bytes) throw new Error('Native controller counter snapshot is inconsistent');
    ids = {};
    for (let index = 0; index < count; index++) {
        const row = native.buffer.add(32 + index * 16);
        ids[row.readU32()] = row.add(8).readU64().toNumber();
    }
    if (failure !== 0 && state === 'running') {
        error = failure === 2 ? 'Unexpected controller query result' : 'Native controller query output or counters are unsupported';
        deactivate();
    }
}
function instruction(address) {
    if (!executable.some(range => address.compare(range.base) >= 0 && address.add(4).compare(range.base.add(range.size)) <= 0))
        throw new Error('Instruction lies outside the controller runtime');
    return Instruction.parse(address);
}
function parse(address, mnemonic, expression) {
    const decoded = instruction(address);
    return decoded.mnemonic === mnemonic ? decoded.opStr.match(expression) : null;
}
const integer = value => Number(value);
function destination(address) {
    const found = instruction(address).opStr.match(/#(0x[0-9a-f]+)$/i);
    return found ? ptr(found[1]) : null;
}
function virtualQuery(target) {
    const rows = [];
    for (let offset = 0; offset < 512; offset += 4) {
        const row = instruction(target.add(offset)); rows.push(row);
        if (row.mnemonic === 'ret') break;
    }
    if (rows.at(-1).mnemonic !== 'ret') return false;
    const saved = {};
    for (const row of rows.slice(0, 10)) {
        if (row.mnemonic !== 'mov') continue;
        const move = row.opStr.match(/^([xw](?:19|2[0-8])), (x0|w1|x2)$/);
        if (move) saved[move[2]] = move[1];
    }
    if (!saved.x0 || !saved.w1 || !saved.x2 || new Set(Object.values(saved).map(value => value.slice(1))).size !== 3) return false;
    for (let index = 0; index + 6 < rows.length; index++) {
        const block = rows.slice(index, index + 7);
        const table = block[2].opStr.match(/^([x][0-9]+), \[x0\]$/);
        const method = block[3].opStr.match(/^([x][0-9]+), \[(x[0-9]+), #(-?(?:0x[0-9a-f]+|[0-9]+))\]$/);
        if (block[0].mnemonic === 'ldr' && block[0].opStr.startsWith('x0, [' + saved.x0 + ', #') &&
            block[1].mnemonic === 'cbz' && block[1].opStr.startsWith('x0, #') &&
            block[2].mnemonic === 'ldr' && table && block[3].mnemonic === 'ldr' && method && method[2] === table[1] &&
            block[4].mnemonic === 'mov' && block[4].opStr === 'w1, ' + saved.w1 &&
            block[5].mnemonic === 'mov' && block[5].opStr === 'x2, ' + saved.x2 &&
            block[6].mnemonic === 'blr' && block[6].opStr === method[1]) {
            const result = rows[index + 7];
            const savedResult = result?.mnemonic === 'mov' ? result.opStr.match(/^(w[0-9]+), w0$/) : null;
            if (savedResult && rows.slice(index + 8).some(row => row.mnemonic === 'mov' && row.opStr === 'w0, ' + savedResult[1])) return true;
        }
    }
    return false;
}
function routingExpression(start, slot, flags) {
    // Track the selected table displacement symbolically instead of matching
    // a numeric firmware-specific offset. Four distinct positive choices must
    // depend on held/not-held and the side flag.
    const registerKey = register => register.replace(/^w/, 'x');
    const values = new Map([[registerKey(flags), {kind: 'side'}]]);
    let condition = null;
    for (let index = 0; index < 32; index++) {
        const row = instruction(start.add(index * 4));
        const written = row.regsAccessed?.written;
        if (!Array.isArray(written) || written.some(register => /^(?:w?sp)$/.test(register))) return false;
        let match;
        if (row.mnemonic === 'ldrb' && (match = row.opStr.match(/^(w[0-9]+), \[sp, #((?:0x[0-9a-f]+|[0-9]+))\]$/))) {
            // A new load replaces the register's provenance even when its
            // source is a different stack slot.
            values.delete(registerKey(match[1]));
            if (integer(match[2]) === slot) values.set(registerKey(match[1]), {kind: 'held'});
            continue;
        }
        if (row.mnemonic === 'mov' && (match = row.opStr.match(/^([xw][0-9]+), #((?:0x[0-9a-f]+|[0-9]+))$/))) {
            values.set(registerKey(match[1]), integer(match[2])); continue;
        }
        if (row.mnemonic === 'tst' && (match = row.opStr.match(/^(w[0-9]+), #((?:0x[0-9a-f]+|[0-9]+))$/))) {
            const value = values.get(registerKey(match[1]));
            condition = value?.kind === 'held' && integer(match[2]) === 1 ? 'held' :
                value?.kind === 'side' && integer(match[2]) === 4 ? 'side' : null;
            if (condition === null) return false;
            continue;
        }
        if (row.mnemonic === 'csel' && (match = row.opStr.match(/^(x[0-9]+), (x[0-9]+), (x[0-9]+), ne$/))) {
            const yes = values.get(registerKey(match[2])), no = values.get(registerKey(match[3]));
            if (condition === null || yes === undefined || no === undefined) return false;
            values.set(registerKey(match[1]), {condition, yes, no}); continue;
        }
        if (row.mnemonic === 'add' && (match = row.opStr.match(/^(x[0-9]+), (x[0-9]+), (x[0-9]+)$/))) {
            const choice = values.get(registerKey(match[3]));
            if (choice?.condition !== 'side' || choice.yes?.condition !== 'held' || choice.no?.condition !== 'held') return false;
            const offsets = [choice.yes.yes, choice.yes.no, choice.no.yes, choice.no.no];
            return offsets.every(value => Number.isInteger(value) && value > 0) && new Set(offsets).size === 4;
        }
        if (/^(str|stur|stp)$/.test(row.mnemonic) && !row.opStr.includes('!') && !row.opStr.includes('],')) continue;
        if (/^(ldr|ldur|ldp)$/.test(row.mnemonic) && !row.opStr.includes('!') && !row.opStr.includes('],')) {
            const destinations = row.opStr.split('[')[0].match(/[xw][0-9]+/g) || [];
            if (destinations.some(register => registerKey(register) === registerKey(flags))) return false;
            for (const register of destinations) values.delete(registerKey(register));
            continue;
        }
        return false;
    }
    return false;
}
function routesFrom(join, slot, flags) {
    const pending = [join], visited = new Set();
    const sideRegister = flags.replace(/^w/, 'x');
    const preservedAcrossCalls = /^w(?:19|2[0-8])$/.test(flags);
    while (pending.length && visited.size < 6144) {
        const address = pending.pop(), key = address.toString();
        if (visited.has(key) || address.compare(join) < 0 || address.compare(join.add(24576)) >= 0) continue;
        visited.add(key);
        const row = instruction(address);
        if (row.mnemonic === 'ldrb' && routingExpression(address, slot, flags)) return true;
        if (['ret', 'br'].includes(row.mnemonic)) continue;
        if (row.mnemonic === 'bl' || row.mnemonic === 'blr') {
            // A returning call preserves x19-x28 under AAPCS64. Follow its
            // continuation only when the side flag has that ownership.
            if (preservedAcrossCalls) pending.push(address.add(4));
            continue;
        }
        const written = row.regsAccessed?.written;
        // Test/compare aliases read their integer operands. Some Capstone
        // builds also list that operand as written for the ANDS/TST alias.
        const readsOnly = /^(cmp|cmn|tst|ccmp|ccmn|cbz|cbnz|tbz|tbnz|b(?:\..+)?)$/.test(row.mnemonic);
        if (!Array.isArray(written)) continue;
        if (!readsOnly && written.some(register =>
            /^(?:w?sp)$/.test(register) || register.replace(/^w/, 'x') === sideRegister)) continue;
        if (/^(b|b\..+|cbz|cbnz|tbz|tbnz)$/.test(row.mnemonic)) {
            const branch = destination(address); if (branch !== null) pending.push(branch);
            if (row.mnemonic === 'b') continue;
        }
        pending.push(address.add(4));
    }
    return false;
}
function inspectCall(call) {
    const side = parse(call.add(4), 'tbz', /^(w[0-9]+), #2, #(0x[0-9a-f]+)$/);
    const cleared = parse(call.sub(4), 'strb', /^wzr, \[sp, #((?:0x[0-9a-f]+|[0-9]+))\]$/);
    if (!side || !cleared || side[1] === 'w1') return null;
    const flags = side[1], slot = integer(cleared[1]);
    const setup = [4, 8, 12, 16].map(offset => instruction(call.sub(4 + offset)));
    if (!setup.some(row => row.mnemonic === 'mov' && /^x0, x(?:19|2[0-8])$/.test(row.opStr)) ||
        !setup.some(row => row.mnemonic === 'add' && row.opStr === 'x2, sp, #' + cleared[1]) ||
        !setup.some(row => row.mnemonic === 'ldr' && row.opStr.startsWith('w1, [sp, #')) ||
        !setup.some(row => row.mnemonic === 'ldr' && row.opStr.startsWith(flags + ', [sp, #'))) return null;
    const left = parse(call.add(8), 'ldrb', /^(w[0-9]+), \[sp, #((?:0x[0-9a-f]+|[0-9]+))\]$/);
    const rightAddress = ptr(side[2]);
    const rightCheck = parse(rightAddress, 'tbz', new RegExp('^' + flags + ', #3, #(0x[0-9a-f]+)$'));
    const right = parse(rightAddress.add(4), 'ldrb', /^(w[0-9]+), \[sp, #((?:0x[0-9a-f]+|[0-9]+))\]$/);
    if (!left || !right || !rightCheck || integer(left[2]) !== slot || integer(right[2]) !== slot) return null;
    const leftStore = parse(call.add(12), 'str', new RegExp('^' + left[1] + ', \\[sp, #((?:0x[0-9a-f]+|[0-9]+))\\]$'));
    const rightStore = parse(rightAddress.add(8), 'str', new RegExp('^' + right[1] + ', \\[sp, #((?:0x[0-9a-f]+|[0-9]+))\\]$'));
    const join = instruction(call.add(16)).mnemonic === 'b' ? destination(call.add(16)) : null;
    if (!leftStore || !rightStore || integer(leftStore[1]) === integer(rightStore[1]) || join === null ||
        !join.equals(ptr(rightCheck[1])) || !join.equals(rightAddress.add(12))) return null;
    const callee = destination(call);
    return callee !== null && virtualQuery(callee) && routesFrom(join, slot, flags) ? {callee, caller: call.add(4)} : null;
}
function resolve() {
    if (resolution !== null) return resolution;
    const matches = [];
    // BL is a public ARM64 encoding; every match is then disassembled and
    // checked against the query ABI, caller and downstream routing evidence.
    for (const range of executable) {
        for (const hit of Memory.scanSync(range.base, range.size, '00 00 00 94 : 00 00 00 fc')) {
            if ((hit.address.toUInt32() & 3) !== 0) continue;
            try { const evidence = inspectCall(hit.address); if (evidence !== null) matches.push(evidence); } catch (_) {}
        }
    }
    if (matches.length !== 1) throw new Error('Controller ABI is ambiguous or unsupported (' + matches.length + ' validated callers)');
    resolution = matches[0]; return resolution;
}
function deactivate() {
    if (['stopped', 'restore-failed', 'restoring'].includes(state)) return;
    if (timer !== null) { clearTimeout(timer); timer = null; }
    if (native !== null) native.disable();
    state = 'restoring';
    if (cleanupTimer !== null) return;
    cleanupDeadline = Date.now() + 5000;
    function finish() {
        cleanupTimer = null;
        try {
            // Keep paired listeners attached until every accepted query leaves.
            // After detach, Gum may omit an outstanding listener's onLeave.
            if (native !== null && native.pending() !== 0) {
                if (Date.now() >= cleanupDeadline) throw new Error('Native controller callbacks did not drain');
                cleanupTimer = setTimeout(finish, 25); return;
            }
            if (hook !== null) { hook.detach(); hook = null; Interceptor.flush(); }
            // A zero work count is not proof that native code has returned.
            // Retain CModule/state through Frida's native script-unload drain.
            state = 'stopped';
        } catch (failure) { error = String(failure); state = 'restore-failed'; }
    }
    cleanupTimer = setTimeout(finish, 0);
}
function heartbeat(seconds) {
    if (!Number.isFinite(seconds) || seconds < 1 || seconds > 30 || state !== 'running') throw new Error('Invalid controller lease');
    if (timer !== null) clearTimeout(timer);
    timer = setTimeout(deactivate, seconds * 1000);
}
rpc.exports = {
    validate() { resolve(); prepareNative(); return {compatible: true, arm64QueryValidated: true,
        nativeCallbacks: true, fridaRuntime: Script.runtime}; },
    activate(seconds) {
        if (state !== 'idle') throw new Error('Controller adapter is already used');
        const evidence = resolve(), callbacks = prepareNative(); state = 'running';
        try {
            callbacks.start(evidence.caller);
            hook = Interceptor.attach(evidence.callee, {onEnter: callbacks.module.onEnter, onLeave: callbacks.module.onLeave});
            callbacks.attached = true;
            heartbeat(seconds);
        } catch (failure) { error = String(failure); deactivate(); throw failure; }
    },
    heartbeat, deactivate,
    status() {
        try { readCounters(); } catch (failure) {
            if (!['stopped', 'restore-failed'].includes(state)) { error = String(failure); deactivate(); }
        }
        return {state, error, queries, changes, controllerIds: ids, nativeCallbacks: true, fridaRuntime: Script.runtime};
    },
    dispose: deactivate
};
