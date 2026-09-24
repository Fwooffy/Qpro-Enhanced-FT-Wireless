param(
    [string]$Version = "0.1.10-poc"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$safeVersion = $Version -replace '[^A-Za-z0-9._-]', '-'
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root "dist"))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $distRoot "QproFaceTracking-$safeVersion"))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts\release-$safeVersion"))

if (-not $releaseRoot.StartsWith($distRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe release target: $releaseRoot"
}
if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releaseRoot, $artifactRoot | Out-Null

Write-Host "Publishing the self-contained Windows hub..."
$hubPublish = Join-Path $artifactRoot "hub"
& dotnet publish (Join-Path $root "qpro-hub\QproFaceTracking.Hub.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $hubPublish
if ($LASTEXITCODE -ne 0) { throw "Publishing the Windows hub failed." }
Copy-Item -LiteralPath (Join-Path $hubPublish "QproFaceTracking.Hub.exe") -Destination (Join-Path $releaseRoot "QproFaceTracking.exe")

Write-Host "Publishing the self-contained label bridge..."
$labelPublish = Join-Path $artifactRoot "label-bridge"
& dotnet publish (Join-Path $root "vd-label-bridge\Qpro.VirtualDesktopLabelBridge.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $labelPublish
if ($LASTEXITCODE -ne 0) { throw "Publishing the label bridge failed." }
$labelDestination = Join-Path $releaseRoot "vd-label-bridge\bin\Release\net10.0"
New-Item -ItemType Directory -Force -Path $labelDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $labelPublish "Qpro.VirtualDesktopLabelBridge.exe") -Destination $labelDestination

Write-Host "Building the combined VRCFT bridge..."
& dotnet build (Join-Path $root "vrcft-gaze-bridge\Qpro.GazeBridge.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "Building the combined VRCFT bridge failed." }
$vrcftBinaryDestination = Join-Path $releaseRoot "vrcft-gaze-bridge\bin\Release\net10.0"
New-Item -ItemType Directory -Force -Path $vrcftBinaryDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $root "vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll") -Destination $vrcftBinaryDestination

function Copy-ReleaseFile([string]$RelativePath) {
    $source = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $source)) { throw "Required release file is missing: $RelativePath" }
    $destination = Join-Path $releaseRoot $RelativePath
    $parent = Split-Path -Parent $destination
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

$runtimeFiles = @(
    "build-and-run.ps1",
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
    "visual_axis_calibration.py",
    "stereo_eye_calibration.py",
    "libquestpro-camera-streamer-v8.so",
    "questpro-camera-relay-v8",
    "questpro-camera-injector",
    "calibration\qpro-independent-visual-axis-v2.json",
    "models\qpro-stereo-tongue-v8-gate.pt",
    "models\qpro-stereo-tongue-v8-direction.pt",
    "research\patch_seacliff_independent_axes.py",
    "LICENSE",
    "THIRD_PARTY_NOTICES.md",
    "release-manifest.json"
)
foreach ($file in $runtimeFiles) { Copy-ReleaseFile $file }
foreach ($file in @("succeed.wav", "trainingComplete.wav", "warning.wav")) {
    Copy-ReleaseFile ("SFX\" + $file)
}
foreach ($file in @("adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "NOTICE.txt", "source.properties")) {
    Copy-ReleaseFile ("platform-tools\" + $file)
}
foreach ($file in @("python-3.12.10-amd64.exe", "LICENSE.txt", "README.txt")) {
    Copy-ReleaseFile ("python-runtime\" + $file)
}
Copy-Item -LiteralPath (Join-Path $root "RELEASE_README.md") -Destination (Join-Path $releaseRoot "README.md")
Copy-Item -LiteralPath (Join-Path $root "UPSTREAM-README.md") -Destination (Join-Path $releaseRoot "UPSTREAM-README.md")

New-Item -ItemType Directory -Force -Path (Join-Path $releaseRoot "captures"), (Join-Path $releaseRoot "training"), (Join-Path $releaseRoot "research\seacliff_eye_model") | Out-Null
Set-Content -LiteralPath (Join-Path $releaseRoot "captures\.gitkeep") -Value ""
Set-Content -LiteralPath (Join-Path $releaseRoot "training\.gitkeep") -Value ""
Set-Content -LiteralPath (Join-Path $releaseRoot "research\seacliff_eye_model\.gitkeep") -Value ""

$forbidden = @(
    Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | Where-Object {
        $_.Extension -in @(".qpcap", ".qplabel", ".jsonl") -or
        $_.Name -eq "bolt-independent-axes.ptl" -or
        $_.Name -eq "GITHUB_PUBLISHING.md" -or
        $_.Name -like "*.csproj" -or
        $_.Name -in @("Program.cs", "TrackingModule.cs", "streamer.c", "relay.c", "injector.c", "build-release.ps1", "build-github-source.ps1") -or
        $_.FullName -match '\\(test_|__pycache__|training\\.+\.(npy|npz))'
    }
)
if ($forbidden.Count) { throw "Private/test artifacts entered the release: $($forbidden.FullName -join ', ')" }

$hashLines = Get-ChildItem -LiteralPath $releaseRoot -Recurse -File |
    Where-Object Name -ne "SHA256SUMS.txt" |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($releaseRoot.Length + 1).Replace('\', '/')
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    }
Set-Content -LiteralPath (Join-Path $releaseRoot "SHA256SUMS.txt") -Value $hashLines -Encoding utf8

$archive = "$releaseRoot.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -LiteralPath $releaseRoot -DestinationPath $archive -CompressionLevel Optimal

Write-Host "RELEASE_READY folder=$releaseRoot"
Write-Host "RELEASE_READY zip=$archive"
