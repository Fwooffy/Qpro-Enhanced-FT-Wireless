# Tests and diagnostics

Run these commands from `src/` in the source checkout or GitHub source export:

| Check | Command | Purpose |
| --- | --- | --- |
| Python unit tests | `python -m unittest discover -p "test_*.py"` | Test capture, calibration, training, and runtime code with synthetic inputs. |
| Cheek calibration compatibility | `dotnet run --project tests/cheek-calibration/CheekCalibrationTests.csproj -c Release` | Check three-pose profiles, source isolation, calibration telemetry and preservation of unsupported four-pose profiles. |
| Camera cheek packets | `dotnet run --project tests/camera-cheeks/CameraCheekTests.csproj -c Release` | Check independent strengths, loopback-only packets, expiry and native fallback. |
| Hub camera selection | `dotnet run --project tests/hub-camera-launch/HubCameraLaunchTests.csproj -c Release` | Check the actual tracking argument builder, paired model paths and independent tongue/camera-cheek switches without launching tracking. |
| Processing profile persistence | `dotnet run --project tests/hub-processing-profiles/HubProcessingProfileTests.csproj -c Release` | Check Responsive defaults, saved choices, legacy settings and unchanged camera/tongue controls without showing a window. |
| Per-feature inference budgets | `python -m unittest test_tracking_budget test_pupil_inference` | Check newest-frame scheduling, rate limits, expiry and cancellation with lower pupil budgets. |
| Runtime restore transactions | `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File test_runtime_recovery.ps1` | Check backup validation, interrupted staging, rollback and private recovery results with isolated synthetic interpreters. |
| Runtime repair and updates | `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File test_runtime_updates.ps1` | Check library repair, backup receipts and restoration without downloading or modifying an installed runtime. |
| Runtime result guidance | `dotnet run --project tests/hub-runtime-recovery-result/HubRuntimeRecoveryResultTests.csproj -c Release` | Check typed repair/restore completion and error explanations. |
| Module heartbeat | `dotnet run --project tests/module-heartbeat/ModuleHeartbeatTests.csproj -c Release -p:VrcftInstallDir="<Steam VRCFaceTracking folder>"` | Check source/DLL/process identity, output observation, stale/closed states and writer lifetime using owned loopback fixtures. |
| Batched Activity output | `dotnet run --project tests/hub-activity-ui/HubActivityUiTests.csproj -c Release -- --offscreen` | Check ordered output, timestamps, retained selection/viewport and yielding to native UI messages. |
| Developer-model promotion | `python -m unittest test_developer_tongue_training` | Check axis, wearer, session and pose regression gates independently of model training. |
| Lower-face training orchestration | `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File test_lower_face_orchestration.ps1` | Execute training helpers with private fixtures; check complete-pair publication, CPU fallback and environment restoration without installing dependencies. |
| VRCFaceTracking mapping tests | `dotnet run --project tests/steam-osc/SteamOscSourceTests.csproj -c Release` | Test Steam Link OSC parsing and face expression mapping without a headset. Requires the .NET 10 SDK. |
| Live override lifecycle | `dotnet run --project tests/live-overlays/LiveOverlayTests.csproj -c Release -p:VrcftInstallDir="<Steam VRCFaceTracking folder>"` | Exercise the production module with private test maps, loopback ports and the installed VRCFT API. Requires Windows and the .NET 10 SDK; does not install a module. |
| Gaze recovery | `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File test_gaze_recovery.ps1` | Exercise the production recovery helpers against a fake headset, including interrupted sessions, ownership checks and reboot recovery. |
| Gaze result guidance | `dotnet run --project tests/hub-gaze/HubGazeTests.csproj -c Release` | Verify the setup popup's next steps for each detected method and refuse incomplete inspection results. |
| Release updater | `dotnet run --project tests/hub-updates/HubUpdateTests.csproj -c Release` | Check release selection, prerelease opt-in, trusted URLs, checksums and package validation with mocked responses. |
| Updater window | `dotnet run --project tests/hub-update-dialog/HubUpdateDialogTests.csproj -c Release` | Check preferences, channel changes, cancellation, staging and compact layout on Windows without downloading updates. |
| Updater lifetime | `dotnet run --project tests/hub-updater-lifetime/HubUpdaterLifetimeTests.csproj -c Release` | Confirm closing and disposing preview windows cancels pending updater work. |
| Release metadata and source | `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File test_release_metadata.ps1` | Check module versions and public source inventories without installing a module. |
| Release ZIP integrity | `python tests/check-release-zip.py <release.zip>` | Read the ZIP and verify its file list, hashes, and local Python imports. |

The runnable release ZIP contains the app and runtime, not these source tests. A passing synthetic test does not establish that a particular headset firmware, streaming app, or avatar works live.
