#define _GNU_SOURCE
#include "sensor_profile.h"
#include "layout_probe.h"
#include "reader_lifetime.h"
#include <dirent.h>
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

// This experimental profile names the shared-page ABI, not an OS release.
// Probing is read-only; matching data does not verify physical signal meaning.
static const char *profile_name = "touch-pro-shared-pages-v1";
static const char *legacy_profile_name = "legacy-51503870024400340";
enum { PAGE_SIZE = QPTP_PAGE_SIZE };
typedef struct {
    const uint8_t *address;
    unsigned long inode;
    unsigned long start, end;
    uint32_t last_counter;
    uint64_t changed_ns;
} Page;
typedef struct { pid_t pid; Page hand[2]; } Source;
static volatile sig_atomic_t stopping = 0;
static void stop_signal(int number) { (void)number; stopping = 1; }
static uint64_t now_ns(void) {
    struct timespec value; clock_gettime(CLOCK_MONOTONIC, &value);
    return (uint64_t)value.tv_sec * UINT64_C(1000000000) + (uint64_t)value.tv_nsec;
}
static int read_file(const char *path, char *out, size_t length) {
    int fd = open(path, O_RDONLY | O_CLOEXEC); if (fd < 0) return 0;
    ssize_t count = read(fd, out, length - 1); close(fd);
    if (count < 0) return 0; out[count] = 0; return 1;
}
static int read_build(char out[128]) {
    int ends[2]; if (pipe2(ends, O_CLOEXEC) != 0) return 0;
    pid_t child = fork();
    if (child == 0) {
        dup2(ends[1], STDOUT_FILENO); close(ends[0]); close(ends[1]);
        execl("/system/bin/getprop", "getprop", "ro.build.version.incremental", (char *)0); _exit(127);
    }
    close(ends[1]);
    if (child < 0) { close(ends[0]); return 0; }
    struct pollfd ready = { ends[0], POLLIN, 0 };
    ssize_t count = poll(&ready, 1, 1500) > 0 ? read(ends[0], out, 127) : -1;
    close(ends[0]);
    // Reap only this bounded read-only helper, never a headset tracking process.
    int status = 0;
    if (count < 0) kill(child, SIGKILL);
    uint64_t deadline = now_ns() + UINT64_C(1500000000);
    pid_t reaped;
    while ((reaped = waitpid(child, &status, WNOHANG)) == 0 && now_ns() < deadline) usleep(10000);
    if (reaped == 0) { kill(child, SIGKILL); waitpid(child, &status, 0); return 0; }
    if (reaped < 0 || count <= 0 || !WIFEXITED(status) || WEXITSTATUS(status)) return 0;
    out[count] = 0;
    while (count > 0 && (out[count - 1] == '\n' || out[count - 1] == '\r' || out[count - 1] == ' ')) out[--count] = 0;
    return 1;
}
static pid_t tracking_pid(void) {
    // /proc is read-only discovery; there is no service restart or code injection.
    DIR *directory = opendir("/proc"); if (!directory) return -1;
    struct dirent *entry; pid_t found = -1;
    while ((entry = readdir(directory))) {
        char *end; long number = strtol(entry->d_name, &end, 10);
        if (!*entry->d_name || *end || number < 1 || number > INT32_MAX) continue;
        pid_t pid = (pid_t)number;
        char path[64], name[256]; snprintf(path, sizeof path, "/proc/%d/cmdline", (int)pid);
        if (!read_file(path, name, sizeof name)) continue;
        const char *base = strrchr(name, '/'); base = base ? base + 1 : name;
        if (!strcmp(base, "trackingservice")) {
            if (found > 0) { closedir(directory); return -1; }
            found = pid;
        }
    }
    closedir(directory);
    return found;
}
static int locate(pid_t pid, int side, Page *result) {
    char path[64], text[1024], expected[96];
    snprintf(path, sizeof path, "/proc/%d/maps", (int)pid);
    snprintf(expected, sizeof expected, "/dev/ashmem/TS_CONTROLLER_%s", side ? "RIGHT" : "LEFT");
    FILE *maps = fopen(path, "r"); if (!maps) return 0;
    int found = 0;
    while (fgets(text, sizeof text, maps)) {
        const char *name = strstr(text, expected);
        if (!name || (name[strlen(expected)] != ' ' && name[strlen(expected)] != '\n')) continue;
        unsigned long begin = 0, end = 0, inode = 0;
        if (sscanf(text, "%lx-%lx %*s %*s %*s %lu", &begin, &end, &inode) != 3 || end - begin != PAGE_SIZE) continue;
        if (found) { found = 0; break; } // Ambiguous pages must not be guessed.
        result->start = begin; result->end = end; result->inode = inode; found = 1;
    }
    fclose(maps); return found;
}
static void release(Source *source) {
    for (int side = 0; side < 2; ++side) {
        if (source->hand[side].address) munmap((void *)source->hand[side].address, PAGE_SIZE);
        source->hand[side] = (Page){0};
    }
    source->pid = -1;
}
static int refresh(Source *source) {
    Page locations[2] = {{0}};
    if (source->pid > 0 && locate(source->pid, 0, &locations[0]) && locate(source->pid, 1, &locations[1])
        && locations[0].inode == source->hand[0].inode && locations[1].inode == source->hand[1].inode
        && locations[0].start == source->hand[0].start && locations[1].start == source->hand[1].start) return 1;
    release(source); source->pid = tracking_pid(); if (source->pid < 1) return 0;
    for (int side = 0; side < 2; ++side) {
        if (!locate(source->pid, side, &locations[side])) { release(source); return 0; }
        char path[96]; snprintf(path, sizeof path, "/proc/%d/map_files/%lx-%lx", (int)source->pid,
            locations[side].start, locations[side].end);
        int fd = open(path, O_RDONLY | O_CLOEXEC); if (fd < 0) { release(source); return 0; }
        void *mapping = mmap(0, PAGE_SIZE, PROT_READ, MAP_SHARED, fd, 0); close(fd);
        if (mapping == MAP_FAILED) { release(source); return 0; }
        source->hand[side] = locations[side]; source->hand[side].address = mapping;
    }
    return 2; // A new producer/mapping requires another capability probe.
}
static QptpSide sample(Page *page, uint64_t now) {
    return qptp_read_sample(page->address, &page->last_counter, &page->changed_ns, now);
}
static int observe_layout(Source *source, QptpLayoutProbe *probe, uint64_t now, int allow_idle_side) {
    for (int side = 0; side < 2; ++side) {
        QptpLayoutSnapshot snapshot;
        Page *page = &source->hand[side];
        if (!qptp_layout_snapshot(page->address, PAGE_SIZE, &snapshot)) {
            qptp_layout_probe_observe(probe, side, 0, (QptpSide){0});
            return 0;
        }
        QptpSide value = sample(page, now);
        // Do not combine publications when the writer advanced during the read.
        if (snapshot.sequence != page->last_counter) return 0;
        // An already-admitted sleeping controller stays neutral without
        // disabling its awake partner. Malformed slots still revoke the pair.
        if (allow_idle_side && !(value.flags & QPTP_VALID)) continue;
        qptp_layout_probe_observe(probe, side, snapshot.sequence, value);
    }
    return qptp_layout_probe_ready(probe);
}
static int wait_layout(Source *source, QptpLayoutProbe *probe) {
    const uint64_t until = now_ns() + UINT64_C(3000000000);
    qptp_layout_probe_init(probe);
    do {
        int refreshed = refresh(source);
        if (refreshed != 1) qptp_layout_probe_init(probe);
        if (refreshed && observe_layout(source, probe, now_ns(), 0)) return 1;
        usleep(10000);
    } while (!stopping && now_ns() < until);
    return 0;
}
static void refused_check(const char *build, const char *reason) {
    printf("QPTP_CHECK_REFUSED build=%s profile=%s reason=%s\n", build, profile_name, reason);
    puts("QPTP_STOPPED");
}
static int send_pair(int client, const uint8_t *bytes, int final) {
    size_t offset = 0;
    uint64_t deadline = now_ns() + UINT64_C(100000000);
    while ((final || !stopping) && offset < QPTP_PACKET_BYTES) {
        uint64_t now = now_ns(); if (now >= deadline) return 0;
        int remaining_ms = (int)((deadline - now + UINT64_C(999999)) / UINT64_C(1000000));
        struct pollfd ready = { client, POLLOUT, 0 };
        if (poll(&ready, 1, remaining_ms) <= 0 || (ready.revents & (POLLERR | POLLHUP | POLLNVAL))) return 0;
        ssize_t count = send(client, bytes + offset, QPTP_PACKET_BYTES - offset, MSG_NOSIGNAL | MSG_DONTWAIT);
        if (count <= 0) return 0; offset += (size_t)count;
    }
    return offset == QPTP_PACKET_BYTES;
}
static int integer(const char *text, int low, int high) {
    char *end; long value = strtol(text, &end, 10); return *text && !*end && value >= low && value <= high ? (int)value : -1;
}
int main(int argc, char **argv) {
    int check = 0, port = 27063, rate = 60; const char *selected = NULL, *stop_file = NULL;
    for (int i = 1; i < argc; ++i) {
        if (!strcmp(argv[i], "--check")) check = 1;
        else if (!strcmp(argv[i], "--profile") && i + 1 < argc) selected = argv[++i];
        else if (!strcmp(argv[i], "--stop-file") && i + 1 < argc) stop_file = argv[++i];
        else if (!strcmp(argv[i], "--port") && i + 1 < argc) port = integer(argv[++i], 1024, 65535);
        else if (!strcmp(argv[i], "--rate") && i + 1 < argc) rate = integer(argv[++i], 40, 90);
        else { fprintf(stderr, "Usage: %s --profile %s [--check] [--port 27063] [--rate 60] [--stop-file PATH]\n", argv[0], profile_name); return 2; }
    }
    if (!selected || (strcmp(selected, profile_name) && strcmp(selected, legacy_profile_name)) || port < 0 || rate < 0) {
        fputs("QPTP_UNSUPPORTED explicit experimental profile and valid arguments required\n", stderr); return 2;
    }
    char build[128] = {0};
    if (!read_build(build) || !qptp_layout_build_identified(build)) {
        fprintf(stderr, "QPTP_UNSUPPORTED build=%s profile=%s; no controller memory was mapped\n", build, selected); return 3;
    }
    if (getuid() != 0) { fputs("QPTP_ROOT_REQUIRED\n", stderr); return 4; }
    signal(SIGINT, stop_signal); signal(SIGTERM, stop_signal);
    Source source = { .pid = -1 };
    int server = -1;
    if (!check) {
        // Own the listening port before mapping anything. A second reader cannot
        // attach to the source while the first supervisor still owns this port.
        server = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
        if (server < 0) { perror("QPTP_SOCKET_FAILED"); return 7; }
        int reuse = 1; setsockopt(server, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof reuse);
        struct sockaddr_in address = { .sin_family = AF_INET, .sin_port = htons((uint16_t)port), .sin_addr.s_addr = htonl(INADDR_LOOPBACK) };
        if (bind(server, (struct sockaddr *)&address, sizeof address) || listen(server, 1)) {
            perror("QPTP_LISTEN_FAILED"); close(server); return 7;
        }
    }
    if (!refresh(&source)) {
        release(&source);
        if (check) refused_check(build, "source-unavailable");
        else fputs("QPTP_SOURCE_UNAVAILABLE controller shared pages could not be mapped read-only\n", stderr);
        if (server >= 0) close(server); return 5;
    }
    QptpLayoutProbe probe;
    if (!wait_layout(&source, &probe)) {
        release(&source);
        if (server >= 0) close(server);
        if (check) refused_check(build, "layout-inconclusive");
        else { fputs("QPTP_LAYOUT_INCONCLUSIVE no fresh consistent controller layout; output stayed disabled\n", stderr); puts("QPTP_STOPPED"); }
        return 6;
    }
    uint64_t sequence = 0;
    if (check) {
        uint64_t now = now_ns(); QptpSide left = sample(&source.hand[0], now), right = sample(&source.hand[1], now);
        if (!(left.flags & QPTP_VALID) || !(right.flags & QPTP_VALID)) {
            release(&source); refused_check(build, "layout-inconclusive"); return 6;
        }
        printf("QPTP_PROFILE build=%s profile=%s experimental=1 live_validated=0 transport=read-only-shared-pages\n", build, profile_name);
        printf("QPTP_CHECK_READY left_flags=%u right_flags=%u left_xy=%.3f,%.3f right_xy=%.3f,%.3f left_samples=%u right_samples=%u\n",
            left.flags, right.flags, left.x, left.y, right.x, right.y,
            qptp_layout_probe_count(&probe, 0), qptp_layout_probe_count(&probe, 1));
        release(&source); return 0;
    }
    printf("QPTP_PROFILE build=%s profile=%s experimental=1 live_validated=0 transport=read-only-shared-pages\n", build, profile_name); fflush(stdout);
    printf("QPTP_LISTENING address=127.0.0.1 port=%d rate=%d\n", port, rate); fflush(stdout);
    int client = -1; uint64_t next = 0, inspected = 0;
    QptpReaderLifetime lifetime; qptp_lifetime_start(&lifetime, now_ns());
    int exit_status = 0, layout_ready = 1;
    while (!stopping && !(stop_file && access(stop_file, F_OK) == 0)) {
        uint64_t now = now_ns();
        if (qptp_lifetime_expired(&lifetime, now)) {
            puts(lifetime.ever_connected ? "QPTP_CLIENT_GONE reader exiting after disconnect" : "QPTP_INITIAL_CONNECT_TIMEOUT reader exiting after 15 seconds");
            if (!lifetime.ever_connected) exit_status = 8;
            break;
        }
        if (now - inspected >= UINT64_C(500000000)) {
            if (refresh(&source) != 1) { layout_ready = 0; qptp_layout_probe_init(&probe); }
            inspected = now;
        }
        if (client < 0) {
            client = accept4(server, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK);
            if (client >= 0) { qptp_lifetime_connect(&lifetime); next = 0; }
        }
        if (client >= 0 && now >= next) {
            QptpPacket packet = { .sequence = ++sequence, .monotonic_ns = now };
            layout_ready = observe_layout(&source, &probe, now, qptp_layout_probe_ready(&probe));
            if (layout_ready) {
                packet.sides[0] = sample(&source.hand[0], now); packet.sides[1] = sample(&source.hand[1], now);
            }
            uint8_t wire[QPTP_PACKET_BYTES]; qptp_encode(&packet, wire);
            if (!send_pair(client, wire, 0)) {
                close(client); client = -1; qptp_lifetime_disconnect(&lifetime, now_ns());
            }
            next = now + UINT64_C(1000000000) / (uint64_t)rate;
        }
        struct pollfd wait = { server, client < 0 ? POLLIN : 0, 0 }; poll(&wait, 1, 3);
    }
    if (client >= 0) {
        QptpPacket neutral = { .sequence = ++sequence, .monotonic_ns = now_ns() }; uint8_t wire[QPTP_PACKET_BYTES];
        qptp_encode(&neutral, wire); send_pair(client, wire, 1); close(client);
    }
    close(server); release(&source); puts("QPTP_STOPPED"); return exit_status;
}
