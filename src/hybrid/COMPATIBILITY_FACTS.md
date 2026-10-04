# Compatibility facts and provenance

This file records the compatibility evidence separately from the independently
authored controller, resolver, adapters and offline tests.

Reference inspected read only:
https://github.com/Yeusepe/QFTPlus/tree/aff54dcc70ea87ac04f93da3063c05a5d30cd7a4/src/tracking/hybrid

No reference scripts/binaries were run and no implementation code was copied.
No gaze source or the previously excluded gaze commit was used.

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
  enabled. Readiness must observe the original path too. The JavaScript adapter
  therefore observes both validated entry points without replacing either.
- The researched PC runtime requests IVRDriverInput_005. The public OpenVR header
  also exposes the compatible skeletal method layout in IVRDriverInput_004.
  The prototype probes either interface and checks executable vtable entries;
  live ABI verification remains required before claiming support.

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
