# Optional command helpers

Most setup is available in Qpro Companion's **First-time setup** page, opened with `../QproFaceTracking.exe`. These commands remain available for manual troubleshooting. Keep this `Helpers` folder next to `QproRuntime` and `QproFaceTracking.exe`; each command finds the scripts in the neighboring runtime folder.

| Helper | Purpose |
| --- | --- |
| `Connect-QproWireless.cmd` | Connect to an already enabled wireless ADB headset. |
| `Pair-QproWireless.cmd` | Pair only when Android shows a six-digit code. Use the temporary pairing IP:port, not the regular connection port. If Connect already reports ready, skip this step. |
| `Enable-QproWireless.cmd` | Enable wireless ADB from an authorized USB connection. |
| `Disable-QproWireless.cmd` | Return the connected headset to USB ADB mode. |
| `Launch-QproWireless.cmd` | Connect to the saved headset and open the Hub. |
| `Install-AMD-ROCm.cmd` | Install the separate AMD runtime after PC runtime setup. |
| `Launch-QproRocm.cmd` | Check the AMD runtime and open the Hub. |

See `../Quest_Pro_Enhanced_Face_Tracking_Guide.pdf` for beginner setup and troubleshooting.
