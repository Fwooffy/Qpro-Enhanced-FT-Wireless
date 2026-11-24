#define _GNU_SOURCE

#include <arpa/inet.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/tcp.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#define CAMERA_WIDTH 400
#define CAMERA_HEIGHT 400
#define SENSOR_WIDTH 2000
#define SENSOR_BYTES ((size_t)SENSOR_WIDTH * CAMERA_HEIGHT)
#define SHARED_HEADER_BYTES ((size_t)80)
#define SHARED_BYTES (SHARED_HEADER_BYTES + SENSOR_BYTES)
#define SHARED_TORN_COUNT_OFFSET ((size_t)56)
#define SHARED_ACTIVE_UNTIL_OFFSET ((size_t)64)
#define SHARED_REQUESTED_MAX_FPS_OFFSET ((size_t)72)
#define CAPTURE_LEASE_NS UINT64_C(2000000000)
#ifndef SHARED_PATH
#define SHARED_PATH "/data/local/tmp/questpro-live-v8-shared.bin"
#endif
#ifndef PID_PATH
#define PID_PATH "/data/local/tmp/questpro-relay-v8.pid"
#endif
#ifndef STREAM_PORT
#define STREAM_PORT 27272
#endif

typedef struct {
    const char *name;
    uint32_t first_camera;
    uint32_t camera_count;
    uint32_t camera_mask;
} StreamMode;

typedef struct {
    pid_t proc_pid;
    pid_t signal_pid;
} RelayProcess;

static uint8_t *g_shared;

static void set_capture_lease(uint8_t *shared, uint64_t active_until) {
    uint64_t *lease = (uint64_t *)(void *)(shared + SHARED_ACTIVE_UNTIL_OFFSET);
    __atomic_store_n(lease, active_until, __ATOMIC_RELEASE);
}

static void handle_termination(int signal_number) {
    (void)signal_number;
    if (g_shared) set_capture_lease(g_shared, 0);
    _exit(0);
}

static int is_decimal_name(const char *text) {
    if (!text || !*text) return 0;
    for (const char *p = text; *p; ++p)
        if (*p < '0' || *p > '9') return 0;
    return 1;
}

static int is_questpro_relay(pid_t pid) {
    char path[64];
    snprintf(path, sizeof(path), "/proc/%d/cmdline", pid);
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return 0;
    char command[512];
    ssize_t length = read(fd, command, sizeof(command) - 1);
    close(fd);
    if (length <= 0) return 0;
    command[length] = '\0';
    const char *name = strrchr(command, '/');
    name = name ? name + 1 : command;
    if (strncmp(name, "questpro-camera-relay", 21) != 0) return 0;
    return name[21] == '\0' || name[21] == '-';
}

static pid_t signal_pid_for_proc(pid_t proc_pid) {
    char path[64];
    snprintf(path, sizeof(path), "/proc/%d/status", proc_pid);
    FILE *status = fopen(path, "r");
    if (!status) return proc_pid;
    char line[512];
    pid_t result = proc_pid;
    while (fgets(line, sizeof(line), status)) {
        if (strncmp(line, "NSpid:", 6) != 0) continue;
        char *cursor = line + 6;
        while (*cursor) {
            char *end = NULL;
            long value = strtol(cursor, &end, 10);
            if (end == cursor) {
                ++cursor;
                continue;
            }
            if (value > 0) result = (pid_t)value;
            cursor = end;
        }
        break;
    }
    fclose(status);
    return result;
}

/* Replace only a relay from an earlier preview run before claiming the port. */
static void stop_previous_relays(void) {
    RelayProcess matches[32];
    size_t count = 0;
    DIR *directory = opendir("/proc");
    if (!directory) return;
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL && count < 32) {
        if (!is_decimal_name(entry->d_name)) continue;
        pid_t proc_pid = (pid_t)strtol(entry->d_name, NULL, 10);
        pid_t signal_pid = signal_pid_for_proc(proc_pid);
        if (signal_pid == getpid() || !is_questpro_relay(proc_pid)) continue;
        if (kill(signal_pid, SIGTERM) == 0) {
            matches[count++] = (RelayProcess){proc_pid, signal_pid};
            printf("STALE_RELAY_STOP_REQUESTED pid=%d\n", signal_pid);
        } else {
            fprintf(stderr, "STALE_RELAY_STOP_FAILED pid=%d error=%s\n",
                    signal_pid, strerror(errno));
        }
    }
    closedir(directory);

    for (unsigned attempt = 0; attempt < 20 && count; ++attempt) {
        size_t alive = 0;
        for (size_t i = 0; i < count; ++i) {
            if (kill(matches[i].signal_pid, 0) == 0 &&
                is_questpro_relay(matches[i].proc_pid))
                matches[alive++] = matches[i];
        }
        count = alive;
        if (count) usleep(50000);
    }
    for (size_t i = 0; i < count; ++i)
        if (is_questpro_relay(matches[i].proc_pid))
            (void)kill(matches[i].signal_pid, SIGKILL);
    if (count) usleep(100000);
}

static void clear_capture_lease_file(void) {
    int fd = open(SHARED_PATH, O_RDWR | O_CLOEXEC);
    if (fd < 0) return;
    void *mapping = mmap(NULL, SHARED_HEADER_BYTES, PROT_READ | PROT_WRITE,
                         MAP_SHARED, fd, 0);
    close(fd);
    if (mapping == MAP_FAILED) return;
    set_capture_lease((uint8_t *)mapping, 0);
    munmap(mapping, SHARED_HEADER_BYTES);
}

static void put_u32(uint8_t *buffer, size_t offset, uint32_t value) {
    memcpy(buffer + offset, &value, sizeof(value));
}

static void put_u64(uint8_t *buffer, size_t offset, uint64_t value) {
    memcpy(buffer + offset, &value, sizeof(value));
}

static uint64_t monotonic_nanoseconds(void) {
    struct timespec value;
    clock_gettime(CLOCK_MONOTONIC, &value);
    return (uint64_t)value.tv_sec * UINT64_C(1000000000) +
           (uint64_t)value.tv_nsec;
}

static int send_all(int fd, const void *data, size_t size) {
    const uint8_t *bytes = (const uint8_t *)data;
    size_t sent = 0;
    while (sent < size) {
        ssize_t result = send(fd, bytes + sent, size - sent, MSG_NOSIGNAL);
        if (result > 0) {
            sent += (size_t)result;
            continue;
        }
        if (result < 0 && errno == EINTR) continue;
        return 0;
    }
    return 1;
}

static uint8_t *shared_memory_failed(const char *operation, int fd) {
    int error = errno;
    struct stat status;
    fprintf(stderr, "SHARED_MEMORY_FAILED operation=%s error=%s errno=%d euid=%u path=%s",
            operation, strerror(error), error, (unsigned)geteuid(), SHARED_PATH);
    if (fd >= 0 && fstat(fd, &status) == 0)
        fprintf(stderr, " owner_uid=%u mode=%04o size=%lld",
                (unsigned)status.st_uid, (unsigned)(status.st_mode & 07777),
                (long long)status.st_size);
    fputc('\n', stderr);
    if (fd >= 0) close(fd);
    errno = error;
    return NULL;
}

static uint8_t *open_shared_memory(void) {
    int fd = open(SHARED_PATH, O_CREAT | O_RDWR | O_CLOEXEC, 0666);
    if (fd < 0) return shared_memory_failed("open", fd);
    struct stat status;
    if (fstat(fd, &status) != 0) return shared_memory_failed("fstat", fd);
    if ((size_t)status.st_size != SHARED_BYTES &&
        ftruncate(fd, (off_t)SHARED_BYTES) != 0)
        return shared_memory_failed("ftruncate", fd);
    /* Some Magisk sessions lack CAP_FOWNER for a file created by Shell.
       Reuse the existing inode without changing an already-correct mode. */
    if ((status.st_mode & 07777) != 0666 && fchmod(fd, 0666) != 0)
        return shared_memory_failed("fchmod", fd);
    void *mapping = mmap(NULL, SHARED_BYTES, PROT_READ | PROT_WRITE,
                         MAP_SHARED, fd, 0);
    if (mapping == MAP_FAILED) return shared_memory_failed("mmap", fd);
    close(fd);
    uint8_t *shared = (uint8_t *)mapping;
    memcpy(shared, "QPSHARV7", 8);
    put_u32(shared, 32, SENSOR_WIDTH);
    put_u32(shared, 36, CAMERA_HEIGHT);
    put_u32(shared, 40, SENSOR_WIDTH);
    put_u32(shared, 44, 1);
    put_u32(shared, 48, SENSOR_BYTES);
    put_u32(shared, 52, 0x1fu);
    return shared;
}

static int open_server(void) {
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    int enabled = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_port = htons(STREAM_PORT);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(fd, (struct sockaddr *)&address, sizeof(address)) != 0 ||
        listen(fd, 1) != 0) {
        int saved_errno = errno;
        close(fd);
        errno = saved_errno;
        return -1;
    }
    return fd;
}

static int open_server_with_retry(void) {
    for (unsigned attempt = 0; attempt < 40; ++attempt) {
        int fd = open_server();
        if (fd >= 0) return fd;
        if (errno != EADDRINUSE) return -1;
        usleep(50000);
    }
    return -1;
}

static int accept_client(int server) {
    int client;
    do {
        client = accept4(server, NULL, NULL, SOCK_CLOEXEC);
    } while (client < 0 && errno == EINTR);
    if (client < 0) return -1;
    int enabled = 1;
    setsockopt(client, IPPROTO_TCP, TCP_NODELAY, &enabled, sizeof(enabled));
    /* Allow bounded PC-side GUI or scheduler stalls without dropping a run. */
    struct timeval timeout = {.tv_sec = 2, .tv_usec = 0};
    setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
    return client;
}

static int copy_stable_frame(const uint8_t *shared, uint32_t *last_generation,
                             uint64_t *sequence, uint64_t *timestamp,
                             uint8_t *frame) {
    const uint32_t *generation = (const uint32_t *)(const void *)(shared + 8);
    uint32_t before = __atomic_load_n(generation, __ATOMIC_ACQUIRE);
    if ((before & 1u) || before == *last_generation) return 0;
    memcpy(sequence, shared + 16, sizeof(*sequence));
    memcpy(timestamp, shared + 24, sizeof(*timestamp));
    memcpy(frame, shared + SHARED_HEADER_BYTES, SENSOR_BYTES);
    uint32_t after = __atomic_load_n(generation, __ATOMIC_ACQUIRE);
    if (before != after || (after & 1u)) return 0;
    *last_generation = after;
    return 1;
}

static int send_frame(int client, const StreamMode *mode, uint64_t sequence,
                      uint64_t timestamp, uint64_t rejected_torn,
                      const uint8_t *frame,
                      uint8_t *payload) {
    const uint32_t width = mode->camera_count * CAMERA_WIDTH;
    const uint32_t payload_bytes = width * CAMERA_HEIGHT;
    const size_t source_x = (size_t)mode->first_camera * CAMERA_WIDTH;
    for (uint32_t y = 0; y < CAMERA_HEIGHT; ++y) {
        memcpy(payload + (size_t)y * width,
               frame + (size_t)y * SENSOR_WIDTH + source_x, width);
    }
    uint8_t header[64] = {0};
    memcpy(header, "QPLIVE3", 7);
    put_u32(header, 8, 3);
    put_u32(header, 12, sizeof(header));
    put_u64(header, 16, sequence);
    put_u64(header, 24, timestamp);
    put_u32(header, 32, width);
    put_u32(header, 36, CAMERA_HEIGHT);
    put_u32(header, 40, width);
    put_u32(header, 44, 1);
    put_u32(header, 48, payload_bytes);
    put_u32(header, 52, mode->camera_mask);
    put_u64(header, 56, rejected_torn);
    return send_all(client, header, sizeof(header)) &&
           send_all(client, payload, payload_bytes);
}

static int parse_mode(const char *value, StreamMode *mode) {
    if (!strcmp(value, "all")) {
        *mode = (StreamMode){"all", 0, 5, 0x1fu};
    } else if (!strcmp(value, "eyes")) {
        *mode = (StreamMode){"eyes", 0, 2, 0x03u};
    } else if (!strcmp(value, "face")) {
        *mode = (StreamMode){"face", 2, 3, 0x1cu};
    } else if (!strcmp(value, "mouth")) {
        *mode = (StreamMode){"mouth", 2, 2, 0x0cu};
    } else {
        return 0;
    }
    return 1;
}

static int parse_arguments(int argc, char **argv, StreamMode *mode,
                           unsigned *max_fps) {
    *mode = (StreamMode){"all", 0, 5, 0x1fu};
    *max_fps = 30;
    for (int i = 1; i < argc; ++i) {
        if (!strcmp(argv[i], "--mode") && i + 1 < argc) {
            if (!parse_mode(argv[++i], mode)) return 0;
        } else if (!strcmp(argv[i], "--max-fps") && i + 1 < argc) {
            char *end = NULL;
            unsigned long value = strtoul(argv[++i], &end, 10);
            if (!end || *end || value > 120) return 0;
            *max_fps = (unsigned)value;
        } else {
            return 0;
        }
    }
    return 1;
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IOLBF, 0);
    if (argc == 2 && !strcmp(argv[1], "--stop")) {
        stop_previous_relays();
        clear_capture_lease_file();
        printf("RELAY_STOPPED capture=idle\n");
        return 0;
    }
    StreamMode mode;
    unsigned max_fps;
    if (!parse_arguments(argc, argv, &mode, &max_fps)) {
        fprintf(stderr, "Usage: %s [--mode all|eyes|face|mouth] [--max-fps 0..120]\n",
                argv[0]);
        return 1;
    }
    stop_previous_relays();
    int pid_file = open(PID_PATH, O_CREAT | O_TRUNC | O_WRONLY | O_CLOEXEC, 0666);
    if (pid_file >= 0) {
        dprintf(pid_file, "%d\n", getpid());
        fchmod(pid_file, 0666);
        close(pid_file);
    }
    uint8_t *shared = open_shared_memory();
    if (!shared) return 2;
    g_shared = shared;
    set_capture_lease(shared, 0);
    put_u32(shared, SHARED_REQUESTED_MAX_FPS_OFFSET, max_fps);
    signal(SIGTERM, handle_termination);
    signal(SIGINT, handle_termination);
    int server = open_server_with_retry();
    if (server < 0) {
        fprintf(stderr, "SERVER_OPEN_FAILED error=%s\n", strerror(errno));
        return 3;
    }
    uint8_t *frame = malloc(SENSOR_BYTES);
    uint8_t *payload = malloc(SENSOR_BYTES);
    if (!frame || !payload) return 4;
    printf("RELAY_LISTENING address=127.0.0.1 port=%d mode=%s max_fps=%u\n",
           STREAM_PORT, mode.name, max_fps);

    const uint64_t minimum_interval = max_fps
        ? UINT64_C(1000000000) / max_fps : 0;
    for (;;) {
        int client = accept_client(server);
        if (client < 0) continue;
        printf("CLIENT_CONNECTED\n");
        set_capture_lease(shared, monotonic_nanoseconds() + CAPTURE_LEASE_NS);
        uint32_t last_generation = 0;
        uint64_t next_send_at = 0;
        while (1) {
            set_capture_lease(shared, monotonic_nanoseconds() + CAPTURE_LEASE_NS);
            uint64_t sequence = 0;
            uint64_t timestamp = 0;
            if (!copy_stable_frame(shared, &last_generation, &sequence,
                                   &timestamp, frame)) {
                usleep(500);
                continue;
            }
            uint64_t now = monotonic_nanoseconds();
            if (minimum_interval && next_send_at && now < next_send_at)
                continue;
            const uint64_t rejected_torn = __atomic_load_n(
                (const uint64_t *)(const void *)(shared + SHARED_TORN_COUNT_OFFSET),
                __ATOMIC_ACQUIRE);
            if (!send_frame(client, &mode, sequence, timestamp, rejected_torn,
                            frame, payload)) {
                fprintf(stderr, "CLIENT_SEND_FAILED error=%s\n", strerror(errno));
                break;
            }
            if (minimum_interval) {
                if (!next_send_at) next_send_at = now;
                do {
                    next_send_at += minimum_interval;
                } while (next_send_at <= now);
            }
        }
        set_capture_lease(shared, 0);
        close(client);
        printf("CLIENT_DISCONNECTED\n");
    }
}
