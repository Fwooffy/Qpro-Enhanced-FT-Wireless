#include "../layout_probe.h"
#include <stdio.h>
#include <sys/types.h>

enum { PAGE_SIZE = QPTP_PAGE_SIZE };

/* Generated from the production reader by build-native.ps1. The fixture uses
 * its real page types, sample decoder and layout observation function. */
#include "reader_types.inc"
#include "reader_probe.inc"

static unsigned checks;
#define CHECK(condition, message) do { ++checks; if (!(condition)) { \
    fprintf(stderr, "FAIL %u: %s\n", checks, message); return 1; } } while (0)

typedef struct { _Alignas(uint32_t) uint8_t bytes[QPTP_PAGE_SIZE]; } FakePage;

static void publish(FakePage *page, uint32_t sequence) {
    for (unsigned slot = 0; slot < 2; ++slot) {
        uint8_t *raw = page->bytes + QPTP_SAMPLES_OFFSET + slot * QPTP_SAMPLE_BYTES;
        qptp_put_u16(raw, 90); qptp_put_u16(raw + 2, 170);
        qptp_put_float(raw + 4, .25f); qptp_put_float(raw + 8, 20.f);
        raw[12] = 1;
    }
    qptp_put_u32(page->bytes + QPTP_COUNTER_OFFSET, sequence);
}

static int observe(Source *source, QptpLayoutProbe *probe, uint64_t now) {
    return observe_layout(source, probe, now, qptp_layout_probe_ready(probe));
}

int main(void) {
    FakePage left = {0}, right = {0};
    Source source = { .pid = 1, .hand = {{ .address = left.bytes }, { .address = right.bytes }} };
    QptpLayoutProbe probe;
    qptp_layout_probe_init(&probe);
    uint64_t now = UINT64_C(1000000000);
    CHECK(!observe(&source, &probe, now), "unpublished pages admitted input");
    for (uint32_t sequence = 1; sequence <= 4; ++sequence) {
        publish(&left, sequence); publish(&right, sequence); now += UINT64_C(10000000);
        CHECK(observe(&source, &probe, now) == (sequence == 4),
            "startup did not require four fresh publications on both sides");
    }
    CHECK(qptp_layout_probe_count(&probe, 0) == 4 && qptp_layout_probe_count(&probe, 1) == 4,
        "startup evidence did not cover both controllers");
    CHECK((sample(&source.hand[0], now).flags & QPTP_VALID) &&
        (sample(&source.hand[1], now).flags & QPTP_VALID), "admitted pair did not produce valid samples");

    /* The admitted right controller can sleep without disabling its awake
     * partner. Its own stale output still becomes neutral after 200 ms. */
    now += UINT64_C(210000000); publish(&left, 5);
    CHECK(observe(&source, &probe, now), "sleeping right controller disabled the awake left");
    CHECK(sample(&source.hand[0], now).flags & QPTP_VALID, "awake left lost its sample");
    CHECK(!(sample(&source.hand[1], now).flags & QPTP_VALID), "sleeping right retained stale input");
    CHECK(qptp_layout_probe_ready(&probe), "sleeping controller discarded prior layout evidence");

    /* Malforming the inactive slot must revoke the pair immediately, even
     * while the selected slot and active controller remain plausible. */
    now += UINT64_C(10000000); publish(&left, 6);
    qptp_put_u16(left.bytes + QPTP_SAMPLES_OFFSET + QPTP_SAMPLE_BYTES, 300);
    CHECK(!observe(&source, &probe, now), "malformed inactive buffer retained pair readiness");
    CHECK(!qptp_layout_probe_ready(&probe) && probe.seen == 0,
        "malformed buffer did not revoke accumulated evidence");
    publish(&left, 7); now += UINT64_C(10000000);
    CHECK(!observe(&source, &probe, now), "repaired left bypassed proof for stale right");
    for (uint32_t sequence = 8; sequence <= 11; ++sequence) {
        publish(&left, sequence); publish(&right, sequence); now += UINT64_C(10000000);
        CHECK(observe(&source, &probe, now) == (sequence == 11),
            "recovery did not require renewed evidence from both controllers");
    }

    /* A same-mapping counter rollback is a producer epoch change. */
    publish(&left, 3); publish(&right, 12); now += UINT64_C(10000000);
    CHECK(!observe(&source, &probe, now), "counter rollback retained admission");
    CHECK(!qptp_layout_probe_ready(&probe), "counter rollback left complete prior evidence");
    for (uint32_t step = 0; step < 4; ++step) {
        publish(&left, 4 + step); publish(&right, 13 + step); now += UINT64_C(10000000);
        CHECK(observe(&source, &probe, now) == (step == 3),
            "post-rollback recovery did not require four fresh left publications");
    }

    /* A rebind resets both the page freshness and shared admission proof. */
    source.hand[0] = (Page){ .address = left.bytes };
    source.hand[1] = (Page){ .address = right.bytes };
    qptp_layout_probe_init(&probe);
    CHECK(!observe(&source, &probe, now), "a rebound source reused old layout admission");
    for (uint32_t step = 1; step <= 3; ++step) {
        publish(&left, 7 + step); publish(&right, 16 + step); now += UINT64_C(10000000);
        CHECK(observe(&source, &probe, now) == (step == 3),
            "rebound source did not collect a new complete pair of proofs");
    }
    printf("PASS %u production reader integration checks; physical signals unverified\n", checks);
    return 0;
}
