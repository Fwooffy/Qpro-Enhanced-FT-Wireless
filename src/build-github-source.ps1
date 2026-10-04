param(
    [string]$Version = "2.1.2"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$safeVersion = $Version -replace '[^A-Za-z0-9._-]', '-'
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root "dist"))
$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $distRoot "QproFaceTracking-$safeVersion-github-source"))

if (-not $sourceRoot.StartsWith($distRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe source-export target: $sourceRoot"
}
if (Test-Path -LiteralPath $sourceRoot) { Remove-Item -LiteralPath $sourceRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $sourceRoot | Out-Null

function Copy-SourceFile([string]$RelativePath, [string]$DestinationPath = $RelativePath) {
    $source = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $source)) { throw "Required source file is missing: $RelativePath" }
    $destination = Join-Path $sourceRoot $DestinationPath
    $parent = Split-Path -Parent $destination
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

$sourceFiles = @(
    "build-and-run.ps1",
    "build-release.ps1",
    "build-guide.py",
    "build-github-source.ps1",
    "Connect-QproWireless.ps1",
    "Connect-QproWireless.cmd",
    "Pair-QproWireless.ps1",
    "Pair-QproWireless.cmd",
    "Launch-QproWireless.ps1",
    "Launch-QproWireless.cmd",
    "Enable-QproWireless.ps1",
    "Enable-QproWireless.cmd",
    "Disable-QproWireless.ps1",
    "Disable-QproWireless.cmd",
    "Install-QproRocm.ps1",
    "Install-AMD-ROCm.cmd",
    "Launch-QproRocm.cmd",
    "generate_sfx.py",
    "enable-quest-wireless.ps1",
    "disable-quest-wireless.ps1",
    "preview-latest-tongue.ps1",
    "native-eye-local-branch-test.ps1",
    "install-vrcft-eye-bridge.ps1",
    "uninstall-vrcft-eye-bridge.ps1",
    "setup-runtime.ps1",
    "runtime-python.ps1",
    "test_runtime_python_discovery.ps1",
    "test_rocm_installer_packages.ps1",
    "test_rocm_gpu_visibility.ps1",
    "test_rocm_runtime_paths.ps1",
    "test_gaze_recovery.ps1",
    "test_lower_face_orchestration.ps1",
    "test_prepare_eye_model.py",
    "test_qpro_eye_engines.py",
    "prepare-eye-model.ps1",
    "prepare_eye_model.py",
    "qpro_eye_engines.py",
    "eye_detector_guard.py",
    "train-latest-tongue-stills.ps1",
    "train-latest-tongue-refinement.ps1",
    "requirements-runtime.txt",
    "python-runtime\README.txt",
    "receiver.py",
    "qpro_gpu.py",
    "pupil_dilation.py",
    "pupil_gpu.py",
    "pupil_inference.py",
    "cheek_camera.py",
    "cheek_still_capture.py",
    "prepare_cheek_stills.py",
    "train_cheek_model.py",
    "train_cheek_pair.py",
    "train-latest-cheeks.ps1",
    "lower_face_training.py",
    "capture_format.py",
    "calibration.py",
    "tongue_calibration.py",
    "tongue_still_capture.py",
    "label_capture.py",
    "tongue_model_preview.py",
    "tongue_image_processing.py",
    "train_tongue_model.py",
    "developer_tongue_training.py",
    "tongue_visibility_calibration.py",
    "train_model.py",
    "prepare_tongue_stills.py",
    "prepare_tongue_training.py",
    "prepare_training.py",
    "calibration_inspect.py",
    "dataset_inspect.py",
    "independent_visual_axis_runtime.py",
    "eye_signal_filter.py",
    "native_eye_probe.py",
    "native_eye_stage_probe.py",
    "native_raw_eye_probe.py",
    "native_eye_pupil_probe.py",
    "open_source_preview.py",
    "hybrid_preview.py",
    "model_preview.py",
    "merge_manual_tongue_caches.py",
    "native_pupil_hybrid_preview.py",
    "native_pupil_independent_preview.py",
    "pupil_gaze_calibration.py",
    "visual_axis_calibration.py",
    "stereo_eye_calibration.py",
    "streamer.c",
    "relay.c",
    "injector.c",
    "research\patch_seacliff_independent_axes.py",
    "research\inspect_seacliff_archives.py",
    "calibration\qpro-independent-visual-axis-v2.json",
    "qpro-hub\QproFaceTracking.Hub.csproj",
    "qpro-hub\Program.cs",
    "vd-label-bridge\Qpro.VirtualDesktopLabelBridge.csproj",
    "vd-label-bridge\Program.cs",
    "vrcft-gaze-bridge\Qpro.GazeBridge.csproj",
    "vrcft-gaze-bridge\module.json",
    "shared\EyebrowPreference.cs",
    "shared\CheekPuffCalibrationProfile.cs",
    "shared\CheekPuffTelemetry.cs",
    "shared\DeveloperCheekPuffBaseline.cs",
    "LICENSE",
    "CONTRIBUTING.md",
    "SECURITY.md",
    "README.md",
    "qpro-hub\README.md",
    "RELEASE_README.md",
    "RELEASE_NOTES_V2.1.2.md",
    "RELEASE_INSTRUCTIONS.md",
    "RELEASE_HELPERS_README.md",
    "DEVELOPER_TONGUE_TRAINING.md",
    "Quest_Pro_Enhanced_Face_Tracking_Guide.pdf",
    "THIRD_PARTY_NOTICES.md",
    "UPSTREAM-README.md",
    "release-manifest.json",
    "tests\README.md",
    "tests\camera-cheeks\CameraCheekTests.csproj",
    "tests\camera-cheeks\Program.cs",
    "tests\model-metadata\ModelMetadataTests.csproj",
    "tests\model-metadata\Program.cs",
    "tests\hub-camera-launch\HubCameraLaunchTests.csproj",
    "tests\hub-camera-launch\Program.cs",
    "tests\live-overlays\LiveOverlayTests.csproj",
    "tests\live-overlays\Program.cs",
    "tests\check-release-zip.py",
    "tests\adb-server-readonly.py",
    "tests\live-smirk-vd.ps1",
    "tests\steam-osc\SteamOscSourceTests.csproj",
    "tests\hub-datasets\HubDatasetTests.csproj",
    "tests\hub-rocm\HubRocmTests.csproj",
    "tests\hub-rocm\Program.cs",
    "tests\hub-gaze\HubGazeTests.csproj",
    "tests\hub-gaze\Program.cs",
    "tests\cheek-calibration\CheekCalibrationTests.csproj",
    "tests\cheek-calibration\Program.cs"
)
foreach ($file in $sourceFiles) { Copy-SourceFile $file }
foreach ($file in @('controller-input.ps1', 'CONTROLLER_INPUT.md',
    'CONTROLLER_INPUT_TEST_NOTES.md', 'HANDS_STARTUP_FIX_NOTES.md', 'HANDS_ADAPTER_FIX_NOTES.md', 'GAZE_ENGINE_TEST_NOTES.md',
    'test_controller_components.py')) { Copy-SourceFile $file }
foreach ($folder in @('hybrid', 'controller-input')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $folder) -Recurse -File | Where-Object {
        $_.Extension -in @('.py', '.js', '.json', '.c', '.cpp', '.h', '.hpp', '.md', '.ps1', '.txt', '.vrdrivermanifest') -or
        $_.Name -eq 'LICENSE'
    }) {
        Copy-SourceFile $file.FullName.Substring($root.Length + 1)
    }
}
foreach ($hubSource in Get-ChildItem -LiteralPath (Join-Path $root "qpro-hub") -File -Filter "*.cs") {
    Copy-SourceFile ("qpro-hub\" + $hubSource.Name)
}
foreach ($bridgeSource in Get-ChildItem -LiteralPath (Join-Path $root "vrcft-gaze-bridge") -File -Filter "*.cs") {
    Copy-SourceFile ("vrcft-gaze-bridge\" + $bridgeSource.Name)
}
foreach ($testSource in Get-ChildItem -LiteralPath (Join-Path $root "tests\steam-osc") -File -Filter "*.cs") {
    Copy-SourceFile ("tests\steam-osc\" + $testSource.Name)
}
foreach ($testSource in Get-ChildItem -LiteralPath (Join-Path $root "tests\hub-datasets") -File -Filter "*.cs") {
    Copy-SourceFile ("tests\hub-datasets\" + $testSource.Name)
}
$exportedReleaseReadme = Join-Path $sourceRoot "RELEASE_README.md"
$releaseGuide = Get-Content -LiteralPath $exportedReleaseReadme -Raw
$releaseGuide = $releaseGuide.Replace("(Docs/UPSTREAM-README.md)", "(UPSTREAM-README.md)").Replace("(Docs/LICENSE)", "(LICENSE)").Replace("(Docs/THIRD_PARTY_NOTICES.md)", "(THIRD_PARTY_NOTICES.md)")
Set-Content -LiteralPath $exportedReleaseReadme -Value $releaseGuide -Encoding utf8
foreach ($test in Get-ChildItem -LiteralPath $root -File -Filter "test_*.py") {
    Copy-SourceFile $test.Name
}
foreach ($sound in @("succeed.wav", "trainingComplete.wav", "warning.wav")) {
    Copy-SourceFile ("SFX\" + $sound)
}
Copy-SourceFile ".gitignore"

$forbidden = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
    $_.Extension -in @(".exe", ".dll", ".so", ".pt", ".qpcap", ".qplabel", ".jsonl", ".npy", ".npz") -or
    $_.Name -eq "bolt-independent-axes.ptl" -or
    $_.FullName -match '\\(__pycache__|captures|training|seacliff_eye_model)\\'
}
if ($forbidden.Count) { throw "Binary/private artifacts entered the GitHub source export: $($forbidden.FullName -join ', ')" }

$userFolderMarker = ':' + [System.IO.Path]::DirectorySeparatorChar + 'Users' + [System.IO.Path]::DirectorySeparatorChar
$textExtensions = @('.json', '.md', '.ps1', '.py', '.txt', '.cmd', '.cs', '.csproj', '.c', '.cpp', '.h', '.hpp', '.js')
foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object Extension -In $textExtensions) {
    $content = [System.IO.File]::ReadAllText($file.FullName).Replace('\\', '\')
    if ($content.IndexOf($userFolderMarker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "A personal Windows user path entered the source export: $($file.FullName.Substring($sourceRoot.Length + 1))"
    }
}

Write-Host "GITHUB_SOURCE_READY folder=$sourceRoot"
Write-Host "The export contains the source tree; place its contents under src/ in the repository."
