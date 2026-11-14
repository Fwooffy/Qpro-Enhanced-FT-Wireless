#ifndef QPRO_TOUCHPAD_LAYOUT_PROBE_H
#define QPRO_TOUCHPAD_LAYOUT_PROBE_H
#include "sensor_profile.h"

/* This probe checks the experimental fixed ABI's observable consistency. It
 * does not prove that fields still describe physical thumb position, contact
 * or pressure on an unfamiliar firmware. No signal is enabled by this header. */
enum { QPTP_LAYOUT_MIN_UPDATES = 4 };

/* A build identifies a diagnostic record, not admission to the sensor layout.
 * In particular, unfamiliar numeric builds still need the same live probe. */
static inline int qptp_layout_build_identified(const char *build) {
    return build && *build && strlen(build) <= 32 &&
        strspn(build, "0123456789") == strlen(build);
}

typedef struct {
    uint32_t sequence;
    QptpSide samples[2];
} QptpLayoutSnapshot;

typedef struct {
    uint32_t sequence[2];
    unsigned updates[2];
    unsigned seen;
} QptpLayoutProbe;

static inline int qptp_layout_decode(const uint8_t raw[QPTP_SAMPLE_BYTES], QptpSide *sample) {
    const unsigned x = qptp_u16(raw), y = qptp_u16(raw + 2);
    if (x > 255 || y > 255 || (raw[12] & ~3u)) return 0;
    const QptpSide value = {
        QPTP_VALID | ((raw[12] & 1u) ? QPTP_CONTACT : 0),
        (float)x / 127.5f - 1.f, (float)y / 127.5f - 1.f,
        qptp_float(raw + 4), qptp_float(raw + 8)
    };
    if (!qptp_side_valid(&value)) return 0;
    *sample = value;
    return 1;
}

/* Read both slots twice under the published counter. Checking only the chosen
 * slot can hide a malformed or incompatible alternate buffer. Equality here
 * also rejects a slot being rewritten before the producer advances its counter.
 * The caller must still verify page ownership/geometry and sample freshness. */
static inline int qptp_layout_snapshot(const uint8_t *page, size_t bytes, QptpLayoutSnapshot *out) {
    if (!page || !out || bytes != QPTP_PAGE_SIZE ||
        (uintptr_t)(page + QPTP_COUNTER_OFFSET) % _Alignof(uint32_t)) return 0;
    const uint32_t *counter = (const uint32_t *)(const void *)(page + QPTP_COUNTER_OFFSET);
    for (int attempt = 0; attempt < 5; ++attempt) {
        uint8_t first[2 * QPTP_SAMPLE_BYTES], second[2 * QPTP_SAMPLE_BYTES];
        const uint32_t sequence = __atomic_load_n(counter, __ATOMIC_ACQUIRE);
        memcpy(first, page + QPTP_SAMPLES_OFFSET, sizeof first);
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        if (sequence != __atomic_load_n(counter, __ATOMIC_RELAXED)) continue;
        memcpy(second, page + QPTP_SAMPLES_OFFSET, sizeof second);
        __atomic_thread_fence(__ATOMIC_ACQUIRE);
        if (sequence != __atomic_load_n(counter, __ATOMIC_RELAXED) ||
            memcmp(first, second, sizeof first)) continue;
        QptpLayoutSnapshot snapshot = { .sequence = sequence };
        if (!qptp_layout_decode(first, &snapshot.samples[0]) ||
            !qptp_layout_decode(first + QPTP_SAMPLE_BYTES, &snapshot.samples[1])) return 0;
        *out = snapshot;
        return 1;
    }
    return 0;
}

static inline void qptp_layout_probe_init(QptpLayoutProbe *probe) {
    *probe = (QptpLayoutProbe){0};
}

static inline void qptp_layout_probe_observe(QptpLayoutProbe *probe, int side,
                                              uint32_t sequence, QptpSide sample) {
    if (side < 0 || side > 1 || !(sample.flags & QPTP_VALID) || !qptp_side_valid(&sample)) {
        qptp_layout_probe_init(probe);
        return;
    }
    const unsigned mask = 1u << side;
    if (!(probe->seen & mask)) {
        /* A zero-initialized page is not evidence of an initial publication.
         * Zero after an observed UINT32_MAX publication is a normal wrap. */
        if (!sequence) { qptp_layout_probe_init(probe); return; }
        probe->seen |= mask;
        probe->sequence[side] = sequence;
        probe->updates[side] = 1;
        return;
    }
    const uint32_t delta = sequence - probe->sequence[side];
    if (!delta) return;
    if (delta >= UINT32_C(0x80000000)) {
        qptp_layout_probe_init(probe);
        return;
    }
    probe->sequence[side] = sequence;
    if (probe->updates[side] < QPTP_LAYOUT_MIN_UPDATES) ++probe->updates[side];
}

static inline int qptp_layout_probe_ready(const QptpLayoutProbe *probe) {
    return probe->seen == 3 && probe->updates[0] >= QPTP_LAYOUT_MIN_UPDATES &&
        probe->updates[1] >= QPTP_LAYOUT_MIN_UPDATES;
}

static inline unsigned qptp_layout_probe_count(const QptpLayoutProbe *probe, int side) {
    return side >= 0 && side < 2 ? probe->updates[side] : 0;
}
#endif
