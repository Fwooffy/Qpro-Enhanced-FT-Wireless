# QproFaceTracking AMD Wireless Edition

This is a clean, local derivative of [Qpro-Enhanced-FT](https://github.com/n0tmast3r/Qpro-Enhanced-FT) v0.1.10-poc. It adds an optional AMD ROCm path for tongue-model tracking and training while retaining the original NVIDIA CUDA and CPU paths. It can carry headset camera and eye data over USB or wireless ADB on a trusted Wi-Fi network. The app is experimental and requires a rooted Quest Pro.

## What's in this package

- The original app, bundled Android Platform-Tools, private Python installer, VRCFaceTracking bridge, and developer v8 demonstration tongue model.
- Local changes for AMD Radeon GPU inference and training, including the Windows ROCm training-exit fix.
- The developer eye-mapping demo profile, with local file paths removed.

The package contains **no personal camera captures, image arrays, training cache, v9 personalized model, generated headset eye patch, or machine-specific Python environment**. It starts with empty `captures/` and `training/` folders. The model supplied here is the upstream developer's v8 demonstration model; personalize it for a different wearer.

## Install on AMD Radeon

The ROCm path was checked on an AMD Radeon RX 7900 XTX with PyTorch 2.9.1 + ROCm 7.2.1. Other GPUs need their own compatibility check against [AMD's Windows support matrix](https://rocm.docs.amd.com/projects/radeon-ryzen/en/latest/docs/compatibility/compatibilityrad/windows/windows_compatibility.html).

1. Extract the whole zip to its own folder. Do not run the app inside the zip. Keep it away from existing QproFaceTracking release folders if you want a fresh installation; the upstream hub can discover data in adjacent releases.
2. Open `QproFaceTracking.exe` and use **Set up PC runtime**. This installs the private Python base used by the app.
3. Close the hub, then double-click `Install-AMD-ROCm.cmd`. It installs a separate `.venv-rocm` in the extracted folder and downloads the pinned AMD wheels. This requires several gigabytes of free space and internet access.
4. Open `QproFaceTracking.exe` normally. Tongue tracking and training select the ROCm environment when it is ready. The Activity log should name your AMD GPU, and live tongue-model loading should say `cuda:0`.

`Launch-QproRocm.cmd` is an optional AMD GPU check and launcher. It is not needed for normal use.

## Install on NVIDIA

Use the original **Set up PC runtime** button in `QproFaceTracking.exe` and skip `Install-AMD-ROCm.cmd`. The original setup detects NVIDIA hardware and installs its CUDA 12.8 PyTorch build. With no ROCm environment in this clean package, the launch and training scripts use that original runtime. CUDA support is retained in the code, but NVIDIA hardware was not available for a live test of this edition. [PyTorch's Windows installation guide](https://pytorch.org/get-started/locally/) describes the CUDA build requirement.

If no supported GPU runtime is available, the original CPU path remains available. It is slower for training.

## Wireless tracking without USB after rooting

1. Keep the rooted Quest and PC on the same trusted Wi-Fi network. On the tested Quest Pro, Singularity had already set the persistent ADB TCP port to `5555`, so wireless ADB came back after a headset reboot without USB. If port 5555 is closed on your headset, open **Singularity > Root Terminal** and run `su -c 'setprop service.adb.tcp.port 5555; stop adbd; start adbd'`. If Magisk prompts, grant Singularity root. This manual step affects the current boot only.
2. Double-click `Connect-QproWireless.cmd` on the PC. Enter the Quest's Wi-Fi IP address; entering only the IP uses port `5555`. You can find the IP in the headset's Wi-Fi connection details. The helper verifies ADB and Magisk root and saves the connection locally.
3. If this PC has never been authorized for wireless debugging and the headset offers **Pair device with pairing code**, double-click `Pair-QproWireless.cmd` instead. Enter the temporary **pairing** `IP:port` and six-digit code from the headset, then enter the regular **connection** `IP:port`. Those two ports are different. Pairing is normally needed once per PC; the connection port can change after a reboot.
4. Double-click `Launch-QproWireless.cmd`. The Hub should show **Quest ADB: Connected** even with the USB cable unplugged. Use its normal **Prepare gaze**, capture, and tracking controls. If it shows **Not connected**, run `Connect-QproWireless.cmd` again with the Quest's current Wi-Fi address, then relaunch the Hub.
5. Open Virtual Desktop on the Quest, connect to the PC over Wi-Fi, and start SteamVR and VRCFaceTracking as required by the upstream guide. In the Hub, start the selected tracking modes. The camera relay uses ADB port forwarding over Wi-Fi; the PC model still runs on the PC GPU or CPU.

After a headset reboot, try `Launch-QproWireless.cmd`. On a headset with persistent wireless ADB like the tested Quest Pro, it should reconnect without touching USB or the headset's terminal. If the saved IP changed, run `Connect-QproWireless.cmd` with the current IP first. If wireless ADB did not return, use the on-headset Root Terminal command above. If your headset exposes Android's **Wireless debugging** screen, its pairing-code method is another cable-free option; use `Pair-QproWireless.cmd` for the first pairing and `Connect-QproWireless.cmd` when its connection port changes.

## USB setup fallback

1. Complete the PC runtime setup above, root setup, Magisk Shell permission, and the upstream VRCFaceTracking bridge instructions. Turn on Quest eye and face tracking. Connect the PC and Quest Pro to the same trusted Wi-Fi network. Keep USB connected for this fallback setup.
2. Approve the **USB debugging** prompt inside the headset. Close any open QproFaceTracking Hub. Double-click `Enable-QproWireless.cmd`. It uses the included `platform-tools/adb.exe`, checks Magisk root, switches the Quest's ADB service to Wi-Fi, and saves its address in `config/wireless-headset.json`.
3. When it says **Wireless ADB ready**, unplug the USB cable. Double-click `Launch-QproWireless.cmd` to open the Hub with that headset selected. Then follow the tracking steps above.

For later sessions, use the cable-free connection steps above. The USB `adb tcpip` mode normally resets on reboot, but Singularity's persistent port setting on the tested Quest brought it back. The on-headset Root Terminal command is a fallback when it does not. To turn off classic wireless ADB for the current boot, close tracking and double-click `Disable-QproWireless.cmd`. Use only a trusted private network because wireless ADB is reachable by other devices on that network while enabled.

Wireless camera and eye relay is separate from Virtual Desktop's PCVR video stream. The latter can use its normal Wi-Fi connection. Wi-Fi quality and router settings will affect camera latency and stability. If the PC cannot reach the Quest, check that the router does not isolate Wi-Fi clients, and rerun the USB setup.

## First tracking session over USB

Follow the [upstream instructions](UPSTREAM-README.md) for root access, the VRCFaceTracking bridge, gaze preparation, capture, and live tracking. Keep Virtual Desktop, SteamVR, and VRCFaceTracking running as required by those steps. Open `QproFaceTracking.exe` normally for the original USB transport. The supplied tongue model is a demo until you capture and train your own data.

The AMD setup downloads packages from `repo.radeon.com`; the original runtime setup downloads Python dependencies and PyTorch. No personal headset data is included in this archive. New captures and models are created only after you use the app.

## Changes from the upstream package

The modified files include `build-and-run.ps1`, the tongue training scripts, `train_tongue_model.py`, `tongue_model_preview.py`, `native_raw_eye_probe.py`, and the Hub source. The Hub executable is rebuilt from this fork's source so its setup and tracking checks accept the selected wireless ADB headset. The AMD runtime installer and wireless pair/connect/setup/launch/disable helpers are included. `SHA256SUMS.txt` lists the exact contents of this AMD Wireless Edition zip.

This edition is not an official release of the upstream repository. See [LICENSE](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md).
