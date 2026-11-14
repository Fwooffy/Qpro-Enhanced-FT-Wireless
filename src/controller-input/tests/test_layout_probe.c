#include "../layout_probe.h"
#include <stdio.h>

static int checks;
#define CHECK(condition, message) do { ++checks; if (!(condition)) { \
    fprintf(stderr, "FAIL %d: %s\n", checks, message); return 1; } } while (0)

static QptpSide valid(void) { return (QptpSide){ QPTP_VALID, .2f, -.3f, .5f, 40.f }; }
static void slot(uint8_t *page, int buffer, unsigned x, unsigned y, float force, float size, uint8_t flags) {
    uint8_t *raw = page + QPTP_SAMPLES_OFFSET + buffer * QPTP_SAMPLE_BYTES;
    qptp_put_u16(raw, (uint16_t)x); qptp_put_u16(raw + 2, (uint16_t)y);
    qptp_put_float(raw + 4, force); qptp_put_float(raw + 8, size); raw[12] = flags;
}
static void ready(QptpLayoutProbe *probe) {
    qptp_layout_probe_init(probe);
    for (uint32_t sequence = 1; sequence <= 4; ++sequence)
        for (int side = 0; side < 2; ++side)
            qptp_layout_probe_observe(probe, side, sequence, valid());
}

int main(void) {
    CHECK(qptp_layout_build_identified("51503870024400340"), "original build lost diagnostic admission");
    CHECK(qptp_layout_build_identified("51503870035800340"), "requested newer build was rejected by number");
    CHECK(qptp_layout_build_identified("99999999999999999"), "an unfamiliar build needs a per-version allowlist");
    CHECK(!qptp_layout_build_identified(NULL) && !qptp_layout_build_identified("") &&
        !qptp_layout_build_identified("51503870035800340\n") &&
        !qptp_layout_build_identified("51503870035800340 diagnostic=injected") &&
        !qptp_layout_build_identified("123456789012345678901234567890123" "45"),
        "missing, malformed or unbounded build identity accepted");
    _Alignas(uint32_t) uint8_t page[QPTP_PAGE_SIZE + 4] = {0};
    slot(page, 0, 0, 255, 0.f, 0.f, 0);
    slot(page, 1, 255, 0, 1.f, 512.f, 1);
    qptp_put_u32(page + QPTP_COUNTER_OFFSET, 1);
    QptpLayoutSnapshot snapshot = {0};
    CHECK(qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "valid double buffers rejected");
    CHECK(snapshot.sequence == 1 && snapshot.samples[0].x == -1.f &&
        snapshot.samples[0].y == 1.f && snapshot.samples[1].x == 1.f &&
        snapshot.samples[1].force == 1.f && snapshot.samples[1].size == 512.f,
        "snapshot decoded the wrong buffers or fields");
    CHECK(!qptp_layout_snapshot(NULL, QPTP_PAGE_SIZE, &snapshot), "null page accepted");
    CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, NULL), "null result accepted");
    CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE - 1, &snapshot), "short page accepted");
    CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE + 1, &snapshot), "oversized page accepted");
    CHECK(!qptp_layout_snapshot(page + 1, QPTP_PAGE_SIZE, &snapshot), "unaligned counter accepted");
    for (int buffer = 0; buffer < 2; ++buffer) {
        slot(page, buffer, 256, 0, 0.f, 0.f, 0);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "invalid x in either slot accepted");
        slot(page, buffer, 0, 256, 0.f, 0.f, 0);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "invalid y in either slot accepted");
        slot(page, buffer, 128, 128, NAN, 0.f, 0);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "NaN force accepted");
        slot(page, buffer, 128, 128, 0.f, INFINITY, 0);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "infinite size accepted");
        slot(page, buffer, 128, 128, -.01f, 0.f, 0);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "negative force accepted");
        slot(page, buffer, 128, 128, 1.01f, 0.f, 0);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "excessive force accepted");
        slot(page, buffer, 128, 128, 0.f, 512.01f, 0);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "excessive size accepted");
        slot(page, buffer, 128, 128, 0.f, -.01f, 0);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "negative size accepted");
        slot(page, buffer, 128, 128, 0.f, 0.f, 4);
        CHECK(!qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "unknown sensor flag accepted");
        slot(page, buffer, 128, 128, .5f, 30.f, 0);
        CHECK(qptp_layout_snapshot(page, QPTP_PAGE_SIZE, &snapshot), "valid slot did not recover");
    }

    QptpLayoutProbe probe;
    qptp_layout_probe_init(&probe);
    CHECK(!qptp_layout_probe_ready(&probe), "empty evidence became ready");
    qptp_layout_probe_observe(&probe, 0, 0, valid());
    CHECK(qptp_layout_probe_count(&probe, 0) == 0, "unpublished zero counter became evidence");
    for (uint32_t sequence = 1; sequence <= 4; ++sequence) {
        qptp_layout_probe_observe(&probe, 0, sequence, valid());
        CHECK(!qptp_layout_probe_ready(&probe), "one-sided evidence became ready");
    }
    for (int duplicate = 0; duplicate < 10; ++duplicate)
        qptp_layout_probe_observe(&probe, 1, 1, valid());
    CHECK(qptp_layout_probe_count(&probe, 1) == 1 && !qptp_layout_probe_ready(&probe),
        "duplicate publications advanced evidence");
    for (uint32_t sequence = 2; sequence <= 3; ++sequence)
        qptp_layout_probe_observe(&probe, 1, sequence, valid());
    CHECK(!qptp_layout_probe_ready(&probe), "three publications on one side became ready");
    qptp_layout_probe_observe(&probe, 1, 4, valid());
    CHECK(qptp_layout_probe_ready(&probe), "four fresh publications per side did not become ready");
    qptp_layout_probe_observe(&probe, 1, 5, valid());
    CHECK(qptp_layout_probe_count(&probe, 1) == 4, "evidence count did not saturate");

    qptp_layout_probe_observe(&probe, 0, 5, (QptpSide){0});
    CHECK(!qptp_layout_probe_ready(&probe) && probe.seen == 0, "stale or invalid sample retained readiness");
    ready(&probe);
    qptp_layout_probe_observe(&probe, 1, 3, valid());
    CHECK(!qptp_layout_probe_ready(&probe) && probe.seen == 0, "counter rollback retained readiness");
    ready(&probe);
    qptp_layout_probe_observe(&probe, 0, 4 + UINT32_C(0x80000000), valid());
    CHECK(!qptp_layout_probe_ready(&probe), "ambiguous half-range sequence jump retained readiness");
    ready(&probe);
    QptpSide bad = valid(); bad.force = NAN;
    qptp_layout_probe_observe(&probe, 0, 5, bad);
    CHECK(!qptp_layout_probe_ready(&probe), "invalid decoded force retained readiness");
    ready(&probe); bad = valid(); bad.flags |= 4;
    qptp_layout_probe_observe(&probe, 1, 5, bad);
    CHECK(!qptp_layout_probe_ready(&probe), "invalid decoded flags retained readiness");
    ready(&probe);
    qptp_layout_probe_observe(&probe, -1, 5, valid());
    CHECK(!qptp_layout_probe_ready(&probe), "invalid side retained readiness");
    CHECK(qptp_layout_probe_count(&probe, -1) == 0 && qptp_layout_probe_count(&probe, 2) == 0,
        "invalid count index was accepted");
    qptp_layout_probe_init(&probe);
    for (unsigned index = 0; index < 4; ++index)
        for (int side = 0; side < 2; ++side)
            qptp_layout_probe_observe(&probe, side, UINT32_MAX - 1 + index, valid());
    CHECK(qptp_layout_probe_ready(&probe), "normal uint32 wrap did not accumulate evidence");
    qptp_layout_probe_init(&probe);
    for (uint32_t sequence = 100; sequence <= 700; sequence += 200)
        for (int side = 0; side < 2; ++side)
            qptp_layout_probe_observe(&probe, side, sequence, valid());
    CHECK(qptp_layout_probe_ready(&probe), "forward counter skips were mistaken for rollback");
    printf("PASS %d layout snapshot and capability evidence checks; physical signals unverified\n", checks);
    return 0;
}
