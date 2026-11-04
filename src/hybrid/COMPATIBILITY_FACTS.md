# Compatibility facts and provenance

This file records the compatibility evidence separately from the independently
authored controller, resolver, adapters and offline tests.

Reference inspected read only:
https://github.com/Yeusepe/QFTPlus/tree/aff54dcc70ea87ac04f93da3063c05a5d30cd7a4/src/tracking/hybrid

No reference scripts/binaries were run and no implementation code was copied.
No gaze source or the previously excluded gaze commit was used.

The dated inspection sections below record historical evidence and the admission
policy used at those times. Their exact-hash and Android-version restrictions
are not the current startup policy. See **Current guarded admission policy** at
the end; the original findings and live-test limits are retained.

## Private compatibility facts

- Virtual Desktop Android1.34.22.0 and the PC driver SHA256 recorded in
  compatibility.json identify the researched build.
- Mono assembly/class/method/field identities and finger result offsets come
  from that build's managed hand path. Resolution uses live Mono metadata.
- The ARM64 query ABI accepts receiver/controller ID/result pointer and returns
  status. Its caller clears a held byte, invokes the query, branches on side
  flags2/3, then uses the held bit and side flag4 for routing. The adapter checks
  these relationships and a virtual-call ABI with a uniquely resolved caller.
- The PC profile describes the controller/hand table, roles, validity fields,
  multimodal fields and skeleton handles in that exact proprietary driver.
  These are compatibility facts, not a stable Virtual Desktop SDK contract.
- A subsequent read-only audit of the admitted local PC driver confirmed those
  frame/validity offsets and its saved original skeletal callable. Optical bone
  generation calls that original directly; the public detoured entry already
  suppresses competing controller skeletons when the native multimodal field is
  enabled. Readiness must observe the original path. The JavaScript adapter
  validates both entry points, then observes only the saved original without
  replacing it. The public diagnostic observer is unnecessary for readiness.
- The originally researched PC runtime requests IVRDriverInput_004. The public OpenVR header
  also exposes the compatible skeletal method layout in IVRDriverInput_004.
  The prototype probes either interface and checks executable vtable entries;
  live ABI verification remains required before claiming support.

## Streamer 1.34.23 inspection

The official beta Streamer download was inspected on 2026-10-10 without installing
it. Both the installer and extracted `driver_VirtualDesktop.dll` had valid Windows
Authenticode signatures from Virtual Desktop, Inc. The extracted driver SHA-256
matches the reported build exactly:

`70698c13cf40e5a21ea0ad241874ffb8ba2f400c2ab7ace7ad4605fc8c77ddd8`

The `.data`, `.detourd`, `.fptable` and `.reloc` sections are identical to the
previously researched driver. Disassembly retains the controller table at
`0x9a740`, driver context at `0x9a6b8`, driver host at `0x9a6e0`, and saved skeletal
callable at `0x9a7e0`. Hand routing still uses the same private field displacements.
This build changes its input-interface lookup from `IVRDriverInput_004` to
`IVRDriverInput_005`; both interfaces are already checked by the Qpro adapter.
The exact fingerprint is independently admitted in `additionalPcDriverSha256s`
with that inspected PC layout. No approximate or family-wide driver match is used.

PC evidence alone does not establish Android compatibility. The matching Quest
APK for Android `1.34.23.0` was not available through the inspected official
download endpoints or public GitHub release assets. Its managed hand-result
layout and controller-query caller must be checked before admitting that headset
build. Android admission remains limited to the first checked `1.34.22.0` build;
there was no live test of a new PC/headset pair. Reinstalling Qpro's Frida
components cannot supply a missing hand profile. Every admitted pair must also
pass the existing live ARM64 caller, Mono metadata, OpenVR object and fresh input
checks before its adapters can report readiness.

Official sources:

https://download.vrdesktop.net/files/beta/VirtualDesktop.Streamer.Setup.exe
https://github.com/guygodin/VirtualDesktop/releases

## Reported PC driver fingerprint inspection

The reported PC driver was found in a local Streamer 1.34.25 installation and
inspected without loading or executing it on 2026-11-04. Its exact SHA-256 is:

`9fa2b70005d4e0aea02a891ae48a5caa0314d0f30e1190001d7ea979ec10d5c1`

Windows Authenticode reports a valid Virtual Desktop, Inc. signature for this
driver and the previously admitted `70698c13...` beta used for comparison. Both
files are 676,376 bytes. Their executable `.text`, private `.data`, `.pdata`,
`.detourc`, `.detourd`, `.fptable`, `.rsrc` and `.reloc` sections are byte-identical,
with identical section RVAs, sizes, flags, image base and image size. All 1,943
runtime-function ranges/unwind references and all 25 functions that refer to the
profile globals match exactly. The only `.rdata` changes are three debug-record
timestamps and a CodeView/PDB GUID. Header changes are build timestamp/checksum;
no private hand fields, entry points, import data or routing instructions changed.

That evidence admits only this exact additional fingerprint with the existing
PC layout. It does not introduce a version-range or approximate binary match.
The matching headset build in the report, Android `1.34.22.0`, remains admitted;
Android `1.34.25.0` is not admitted by this PC-only inspection. Existing runtime
object, role, interface, original-callable, ARM64 caller and fresh optical-input
checks remain required. The read-only compatibility report continues to set
`runtimeValidated` to false. This offline comparison does not establish live
finger accuracy or performance.

## Public interface facts

Valve documents a31-bone left/right hand skeleton and
IVRDriverInput::UpdateSkeletonComponent with WithController/WithoutController
motion ranges. Its public DriverPose_t validity flags are at bytes276 and279 for
the admitted win64 layout; the hook additionally checks the supplied pose size.

https://github.com/ValveSoftware/openvr/wiki/Creating-a-Skeletal-Input-Driver
https://github.com/ValveSoftware/openvr/wiki/Hand-Skeleton
https://raw.githubusercontent.com/ValveSoftware/openvr/master/headers/openvr_driver.h

## Validation boundary

The offline fixtures establish fail-closed gates, supervision/cleanup ordering,
resolver guards and source syntax. Synthetic memory/instructions cannot establish
that the current headset runtime supplies correct fingers or that every firmware
restores successfully. Until a live test passes, this remains experimental and
runtime-unvalidated; future driver or firmware changes need new evidence.

## Current guarded admission policy

V3.0.1 separates version labels from the private layouts used by native hooks.
The inspected full-file hashes remain provenance. An unfamiliar PC full hash
may proceed only when a conservative PE code/layout-family fingerprint matches
an approved layout; it is not a broad version range or an approximate code
match. Startup additionally validates the actual loaded image and native
interfaces before installing fixed-RVA hooks. A code/layout mismatch still
needs review and cannot be bypassed with a version string.

Android `1.34.22.0` remains the researched reference. Another app version label
may attempt guarded startup, but the actual Mono hand-result fields, types,
sizes and layout must be established before the adapter uses result offsets.
The native query caller, controller roles, bone data, fresh optical input,
leases and confirmed cleanup must still pass. Frida, component and settings
requirements are unchanged. An unsupported layout is rejected cleanly.

The manual compatibility command remains read-only preflight and reports
`runtimeValidated: false`. Neither green prechecks nor a family fingerprint
establish live finger accuracy, performance, successful restoration on another
firmware, or compatibility with all future Virtual Desktop releases. The dated
PC-only inspections above still do not prove a new PC/headset pair works live.
