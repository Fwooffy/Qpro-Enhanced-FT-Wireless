# Contributing

Issues and pull requests are welcome. Please state which Quest firmware, streaming-app and VRChat versions, avatar output mode, connection type, GPU, and model you tested.

Do not submit raw inward-camera captures, extracted Meta binaries/models, personal calibration data, or checkpoints trained on people who did not consent to public redistribution. Synthetic fixtures and summarized diagnostics are preferred.

Before opening a pull request:

1. From `src/`, run `python -m unittest discover -p "test_*.py"` in a Qpro Python environment.
2. Build `src/qpro-hub/QproFaceTracking.Hub.csproj` in Release mode.
3. Verify that Stop restores the stock eye model, disables tongue output, stops the relay, and clears Qpro ADB forwarding. Check that ordinary native forwarding resumes after Stop and ends on Hub close. Test native standard face/eye sessions without ADB or Python separately from camera sessions.
4. Avoid expanding firmware support without a hardware test and exact build number.

The GitHub source includes tests and build scripts. The runnable release ZIP omits source code, tests, private captures, and personal models. The direct face bridge must remain the single owner of final face state during Companion sessions. Retained VRCFaceTracking modules and recovery tests cover older installations; installing one is not a new setup requirement. Gaze and tongue additions must not overwrite stock jaw, lip, cheek, brow, or blink values.
