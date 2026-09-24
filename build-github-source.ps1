param(
    [string]$Version = "0.1.10"
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
    "setup-runtime.ps1",
    "prepare-eye-model.ps1",
    "train-latest-tongue-stills.ps1",
    "train-latest-tongue-refinement.ps1",
    "requirements-runtime.txt",
    "receiver.py",
    "capture_format.py",
    "calibration.py",
    "tongue_calibration.py",
    "tongue_still_capture.py",
    "label_capture.py",
    "tongue_model_preview.py",
    "train_tongue_model.py",
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
    "vrcft-gaze-bridge\TrackingModule.cs",
    "vrcft-gaze-bridge\module.json",
    "LICENSE",
    "CONTRIBUTING.md",
    "SECURITY.md",
    "THIRD_PARTY_NOTICES.md",
    "UPSTREAM-README.md",
    "release-manifest.json"
)
foreach ($file in $sourceFiles) { Copy-SourceFile $file }
foreach ($test in Get-ChildItem -LiteralPath $root -File -Filter "test_*.py") {
    Copy-SourceFile $test.Name
}
foreach ($sound in @("succeed.wav", "trainingComplete.wav", "warning.wav")) {
    Copy-SourceFile ("SFX\" + $sound)
}
Copy-SourceFile "RELEASE_README.md" "README.md"
Copy-SourceFile ".gitignore"

$forbidden = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
    $_.Extension -in @(".exe", ".dll", ".so", ".pt", ".qpcap", ".qplabel", ".jsonl", ".npy", ".npz") -or
    $_.Name -eq "bolt-independent-axes.ptl" -or
    $_.FullName -match '\\(__pycache__|captures|training|seacliff_eye_model)\\'
}
if ($forbidden.Count) { throw "Binary/private artifacts entered the GitHub source export: $($forbidden.FullName -join ', ')" }

Write-Host "GITHUB_SOURCE_READY folder=$sourceRoot"
Write-Host "Upload the contents of this folder to the repository root; do not upload the folder as one nested directory."
