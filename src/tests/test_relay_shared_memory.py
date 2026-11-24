"""Exercise the relay's actual buffer setup with simulated permission failures.

Run with a host C compiler, for example:
    python tests/test_relay_shared_memory.py --cc path/to/zig.exe
The compiler and all output stay outside the installed headset runtime.
"""

import argparse
import os
from pathlib import Path
import subprocess
import tempfile


SHIM = r'''
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>

#define O_CREAT 1
#define O_RDWR 2
#define O_CLOEXEC 4
#define PROT_READ 1
#define PROT_WRITE 2
#define MAP_SHARED 1
#define MAP_FAILED ((void *)-1)
#define SHARED_PATH "/fixture/shared.bin"
#define SENSOR_WIDTH 2000
#define CAMERA_HEIGHT 400
#define SENSOR_BYTES ((size_t)800000)
#define SHARED_BYTES ((size_t)800080)

struct stat { unsigned st_uid; unsigned st_mode; long long st_size; };
static struct stat file;
static unsigned char buffer[SHARED_BYTES];
static const char *failed;
static int changes, resizes, closes;
static int denied(const char *operation) {
    if (strcmp(operation, failed)) return 0;
    errno = EPERM;
    return 1;
}
static int open(const char *path, int flags, int mode) {
    (void)path; (void)flags; (void)mode;
    return denied("open") ? -1 : 12;
}
static unsigned geteuid(void) { return 0; }
static int fstat(int fd, struct stat *result) {
    (void)fd;
    if (denied("fstat")) return -1;
    *result = file;
    return 0;
}
static int ftruncate(int fd, off_t size) {
    (void)fd;
    ++resizes;
    if (denied("ftruncate")) return -1;
    file.st_size = size;
    return 0;
}
static int fchmod(int fd, int mode) {
    (void)fd;
    ++changes;
    if (denied("fchmod")) return -1;
    file.st_mode = (unsigned)mode;
    return 0;
}
static void *mmap(void *address, size_t size, int protection, int flags, int fd, off_t offset) {
    (void)address; (void)size; (void)protection; (void)flags; (void)fd; (void)offset;
    return denied("mmap") ? MAP_FAILED : buffer;
}
static int close(int fd) {
    (void)fd;
    ++closes;
    errno = EIO; /* Cleanup must not replace the original permission failure. */
    return 0;
}
static void put_u32(uint8_t *bytes, size_t offset, uint32_t value) {
    memcpy(bytes + offset, &value, sizeof(value));
}
'''

CASES = r'''
int main(int argc, char **argv) {
    if (argc != 2) return 99;
    const char *test = argv[1];
    file = (struct stat){2000, 0666, SHARED_BYTES};
    failed = test;
    if (!strcmp(test, "fresh")) { file.st_mode = 0644; file.st_size = 0; }
    if (!strcmp(test, "fchmod")) file.st_mode = 0644;
    if (!strcmp(test, "ftruncate")) file.st_size = 0;
    if (!strcmp(test, "already-writable")) failed = "fchmod";
    uint8_t *result = open_shared_memory();
    int error = errno;
    int success = !strcmp(test, "fresh") || !strcmp(test, "already-writable");
    if (!!result != success) return 1;
    if (success) {
        if (memcmp(result, "QPSHARV7", 8)) return 2;
        if (file.st_mode != 0666 || file.st_size != SHARED_BYTES) return 3;
        if (changes != (!strcmp(test, "fresh") ? 1 : 0)) return 4;
        if (resizes != (!strcmp(test, "fresh") ? 1 : 0)) return 5;
    } else if (error != EPERM) return 6;
    if (closes != (!strcmp(test, "open") ? 0 : 1)) return 7;
    return 0;
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cc", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    relay = (root / "relay.c").read_text(encoding="utf-8")
    implementation = relay[relay.index("static uint8_t *shared_memory_failed("):
                           relay.index("static int open_server(")]
    with tempfile.TemporaryDirectory(prefix="qpro-relay-permissions-") as directory:
        source = Path(directory) / "permission_fixture.c"
        executable = Path(directory) / ("fixture.exe" if os.name == "nt" else "fixture")
        source.write_text(SHIM + implementation + CASES, encoding="utf-8")
        command = [str(args.cc)]
        if args.cc.stem.lower() == "zig":
            command.append("cc")
        subprocess.run(command + ["-std=c11", "-Wall", "-Wextra", "-Werror", str(source),
                                  "-o", str(executable)], check=True, timeout=120)
        cases = ("already-writable", "fresh", "open", "fstat", "ftruncate", "fchmod", "mmap")
        for case in cases:
            result = subprocess.run([str(executable), case], capture_output=True, text=True, timeout=10)
            if result.returncode:
                raise AssertionError(f"{case} failed ({result.returncode}): {result.stderr}")
            if case not in ("already-writable", "fresh"):
                required = ("SHARED_MEMORY_FAILED", "operation=" + case, "errno=", "euid=0", "path=/fixture/shared.bin")
                if not all(part in result.stderr for part in required):
                    raise AssertionError(f"{case} lost its diagnostic: {result.stderr}")
            elif result.stderr:
                raise AssertionError(f"{case} emitted a false error: {result.stderr}")
        print(f"PASS: {len(cases)} compiled shared-memory cases, including denied redundant chmod and saved errno.")


if __name__ == "__main__":
    main()
