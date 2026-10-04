'use strict';
// Independently validate the ARM64 ABI and the caller's two-sided routing.
// No firmware address or unvalidated substring match is used as a target.
if (Process.arch !== 'arm64') throw new Error('Hands require an ARM64 runtime');
const library = Process.getModuleByName('libvrapiimpl.so');
const executable = library.enumerateRanges('r-x');
let state = 'idle', error = null, hook = null, timer = null, resolution = null;
let queries = 0, changes = 0;
const ids = {};
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
    if (timer !== null) { clearTimeout(timer); timer = null; }
    if (hook !== null) { hook.detach(); hook = null; }
    state = 'stopped';
}
function heartbeat(seconds) {
    if (!Number.isFinite(seconds) || seconds < 1 || seconds > 30 || state !== 'running') throw new Error('Invalid controller lease');
    if (timer !== null) clearTimeout(timer);
    timer = setTimeout(deactivate, seconds * 1000);
}
rpc.exports = {
    validate() { resolve(); return {compatible: true, arm64QueryValidated: true}; },
    activate(seconds) {
        if (state !== 'idle') throw new Error('Controller adapter is already used');
        const evidence = resolve(); state = 'running';
        try {
            hook = Interceptor.attach(evidence.callee, {
                onEnter(args) { this.output = args[2]; this.id = args[1].toUInt32(); this.applies = this.returnAddress.equals(evidence.caller); },
                onLeave(result) {
                    if (state !== 'running' || !this.applies || result.toInt32() !== 0) return;
                    try {
                        const held = this.output.readU8();
                        if (held !== 0 && held !== 1) throw new Error('Unexpected controller query result');
                        queries++; ids[this.id] = (ids[this.id] || 0) + 1;
                        if (held === 0) { this.output.writeU8(1); changes++; }
                    } catch (failure) { error = String(failure); deactivate(); }
                }
            });
            heartbeat(seconds);
        } catch (failure) { error = String(failure); deactivate(); throw failure; }
    },
    heartbeat, deactivate,
    status() { return {state, error, queries, changes, controllerIds: ids}; },
    dispose: deactivate
};
