param(
    [switch]$NoWindow,
    [switch]$SkipPythonSetup,
    [switch]$RebuildNative,
    [switch]$RebuildManaged,
    [ValidateSet("all", "face", "eyes", "mouth")]
    [string]$CameraMode = "all",
    [ValidateRange(0, 120)]
    [int]$MaxFps = 30,
    [ValidateRange(1024, 65535)]
    [int]$StreamPort = 27273,
    [string]$AdbTarget = "",
    [switch]$Record,
    [ValidateRange(0, 3600)]
    [int]$RecordSeconds = 0,
    [string]$RecordPath = "",
    [ValidateRange(1024, 65535)]
    [int]$LabelsPort = 27274,
    [switch]$NoLabels,
    [ValidateSet("VirtualDesktop", "SteamLink")]
    [string]$TrackingSource = "VirtualDesktop",
    [switch]$Calibration,
    [switch]$TongueCalibration,
    [switch]$TongueStillCalibration,
    [switch]$TongueCorrectionCalibration,
    [switch]$TongueRefinementCalibration,
    [switch]$TongueArcCalibration,
    [switch]$CheekStillCalibration,
    [switch]$ModelPreview,
    [string]$ModelPath = ".\models\qpro-five-camera-pilot.pt",
    [string]$ModelDevice = "auto",
    [switch]$TonguePreview,
    [switch]$EnableTongueOutput,
    [switch]$EnableCheekOutput,
    [switch]$PupilOutput,
    [ValidateRange(1.0, 3.0)]
    [double]$PupilSensitivity = 1.4,
    [string]$TongueModelPath = ".\models\qpro-stereo-tongue-v3.pt",
    [string]$TongueDirectionModelPath = ".\models\qpro-stereo-tongue-v4-hybrid.pt",
    [string]$TongueModelDevice = "auto",
    [ValidateRange(0, 100)]
    [int]$TongueSmoothing = 55,
    [ValidateSet("camera", "native", "weighted", "agreement")]
    [string]$TongueVisibilityMode = "weighted",
    [string]$StopFile = "",
    [ValidateRange(0, 2147483647)]
    [int]$CompanionPid = 0,
    [ValidateRange(0, 9223372036854775807)]
    [long]$CompanionStartFileTime = 0,
    [switch]$OpenSourcePreview,
    [switch]$HybridPreview,
    [string]$HybridCalibrationPath = ".\calibration\qpro-hybrid-eye-calibration.json",
    [string]$NextModelPath = ".\third_party\EyeTrackVR-Beta5\EyeTrackApp\Models\end2end_model.onnx",
    [string]$FaceModelPath = ".\third_party\ProjectBabble\BabbleApp\Models\EFFB0E11BS128V7.5\onnx\model.onnx",
    [switch]$EyeCalibration,
    [string]$CalibrationOverlayPath = ".\third_party\BabbleCalibration-Windows-1.0.8\BabbleCalibration.x86_64.exe",
    [string]$EyeCalibrationOutput = ".\calibration\qpro-hybrid-eye-calibration.json",
    [ValidateRange(10, 600)]
    [int]$GazeCalibrationSeconds = 60,
    [ValidateRange(10, 600)]
    [int]$ConvergenceCalibrationSeconds = 40
)

$ErrorActionPreference = "Continue"
if (($CompanionPid -gt 0) -ne ($CompanionStartFileTime -gt 0)) {
    throw 'CompanionPid and CompanionStartFileTime must be supplied together.'
}
. (Join-Path $PSScriptRoot 'runtime-python.ps1')
$relayStarted = $false
$relayProcess = $null
$relayClientOutputPath = Join-Path $PSScriptRoot "questpro-relay-client-output.txt"
$relayClientErrorPath = Join-Path $PSScriptRoot "questpro-relay-client-error.txt"
$labelBridgeProcess = $null
$launcherFailure = $null
$hadAndroidSerial = Test-Path Env:ANDROID_SERIAL
$previousAndroidSerial = $env:ANDROID_SERIAL
$qproGpuVisibilityNames = @('HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL')
$qproSavedGpuVisibility = @{}
foreach ($name in $qproGpuVisibilityNames) {
    $qproSavedGpuVisibility[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Restore-QproGpuVisibility {
    foreach ($name in $qproGpuVisibilityNames) {
        [Environment]::SetEnvironmentVariable($name, $qproSavedGpuVisibility[$name], 'Process')
    }
}
if ([string]::IsNullOrWhiteSpace($AdbTarget) -and -not [string]::IsNullOrWhiteSpace($env:QPRO_ADB_TARGET)) {
    $AdbTarget = $env:QPRO_ADB_TARGET.Trim()
}

function Find-AdbExecutable {
    if (-not [string]::IsNullOrWhiteSpace($env:QPRO_ADB) -and (Test-Path -LiteralPath $env:QPRO_ADB)) {
        return [System.IO.Path]::GetFullPath($env:QPRO_ADB)
    }

    $bundledPath = Join-Path $PSScriptRoot "platform-tools\adb.exe"
    if (Test-Path -LiteralPath $bundledPath) { return $bundledPath }

    $command = Get-Command adb -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $standardPath = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
    if (Test-Path -LiteralPath $standardPath) { return $standardPath }

    throw "Android Platform Tools were not found. Re-extract the release so platform-tools\adb.exe is present."
}

function Resolve-WorkspacePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return "" }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Path))
}

function Get-ExtendedFilePath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath.Length -ge 260 -and -not $fullPath.StartsWith('\\?\')) {
        return '\\?\' + $fullPath
    }
    return $fullPath
}

function Get-FileSha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead((Get-ExtendedFilePath $Path))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-ShortLabelBridgePath([string]$SourcePath) {
    if ($SourcePath.Length -lt 240) { return $SourcePath }
    $shortRoot = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime\bin'
    New-Item -ItemType Directory -Force -Path $shortRoot | Out-Null
    $sourceHash = Get-FileSha256 $SourcePath
    $shortPath = Join-Path $shortRoot ("QproLabelBridge-$($sourceHash.Substring(0, 16)).exe")
    if (-not (Test-Path -LiteralPath $shortPath)) {
        [System.IO.File]::Copy((Get-ExtendedFilePath $SourcePath), $shortPath, $false)
    }
    if ((Get-FileSha256 $shortPath) -ne $sourceHash) {
        throw "The short-path label bridge copy failed verification: $shortPath"
    }
    Write-Host "Using short Virtual Desktop label bridge path: $shortPath"
    return $shortPath
}

if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
    $env:ANDROID_SERIAL = $AdbTarget.Trim()
}
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$adbExecutable = Find-AdbExecutable

$nativeArtifacts = @(
    (Join-Path $PSScriptRoot "libquestpro-camera-streamer-v8.so"),
    (Join-Path $PSScriptRoot "questpro-camera-relay-v8"),
    (Join-Path $PSScriptRoot "questpro-camera-injector")
)
$needsNativeBuild = $RebuildNative -or @($nativeArtifacts | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -gt 0
$clang = $null
if ($needsNativeBuild) {
    $ndkRoot = $env:ANDROID_NDK_HOME
    if (-not $ndkRoot) {
        $ndkBase = Join-Path $env:LOCALAPPDATA "Android\Sdk\ndk"
        if (Test-Path $ndkBase) {
            $ndkRoot = Get-ChildItem $ndkBase -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if (-not $ndkRoot) { throw "Prebuilt headset binaries are missing and Android NDK was not found." }
    $clang = Join-Path $ndkRoot "toolchains\llvm\prebuilt\windows-x86_64\bin\clang.exe"
    if (-not (Test-Path $clang)) { throw "clang.exe not found under $ndkRoot" }
}

Push-Location $PSScriptRoot
try {
    Write-Host "QproFaceTracking launcher v2.35 (stream port $StreamPort)"
    $sourceConfig = Join-Path $env:LOCALAPPDATA "QproFaceTracking\config\tracking-source.txt"
    $savedSource = if (Test-Path -LiteralPath $sourceConfig -PathType Leaf) {
        (Get-Content -LiteralPath $sourceConfig -Raw).Trim().ToLowerInvariant()
    } else { "virtual-desktop" }
    $requestedSource = if ($TrackingSource -eq "SteamLink") { "steam-link" } else { "virtual-desktop" }
    if ($savedSource -ne $requestedSource) {
        throw "The launcher selected $TrackingSource, but the Qpro module is configured for $savedSource. Install the matching source module in First-time setup, then restart VRCFaceTracking before retrying."
    }
    Write-Host "Tracking source: $TrackingSource"
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        $adbState = & $adbExecutable get-state 2>&1
        if ($LASTEXITCODE -ne 0 -or ($adbState -join "`n").Trim() -ne "device") {
            throw "ADB target is unavailable: $AdbTarget. Reconnect it or use USB."
        }
        Write-Host "ADB target: $AdbTarget"
    }
    else {
        $adbState = & $adbExecutable get-state 2>&1
        if ($LASTEXITCODE -ne 0 -or ($adbState -join "`n").Trim() -ne "device") {
            throw "No authorized Quest was found over ADB. Connect it by USB or pass -AdbTarget."
        }
    }

    $rootProbe = & $adbExecutable shell su -c id 2>&1
    $rootProbeText = ($rootProbe -join "`n").Trim()
    if ($LASTEXITCODE -ne 0 -or $rootProbeText -notmatch 'uid=0\(root\)') {
        $transportHint = if ([string]::IsNullOrWhiteSpace($AdbTarget)) { "USB" } else { $AdbTarget }
        throw "Magisk root is not granted to Android Shell on $transportHint. On the headset open Magisk > Superuser and enable Shell (or ADB Shell), then retry. The camera relay and provider injector were not started."
    }

    if ($TongueCalibration) { $CameraMode = "face" }
    if ($TongueStillCalibration -or $TongueCorrectionCalibration -or $TongueRefinementCalibration -or $TongueArcCalibration -or $CheekStillCalibration) {
        # Guided stills train on cameras 2 and 3 only. The third face panel
        # adds 50% more wireless data without contributing to tongue targets.
        $CameraMode = "mouth"
        if (-not $PSBoundParameters.ContainsKey("MaxFps")) { $MaxFps = 20 }
    }
    if ($TonguePreview) {
        $CameraMode = if ($PupilOutput) { "all" } else { "mouth" }
        if (-not $PSBoundParameters.ContainsKey("MaxFps")) { $MaxFps = 20 }
    }
    elseif ($PupilOutput -and -not $PSBoundParameters.ContainsKey("CameraMode")) {
        $CameraMode = "eyes"
    }
    if ($PupilOutput -and $CameraMode -notin @("eyes", "all")) {
        throw "Pupil output needs both eye cameras. Use -CameraMode eyes or all."
    }

    $recordEnabled = $Record -or $Calibration -or $TongueCalibration -or $TongueStillCalibration -or $TongueCorrectionCalibration -or $TongueRefinementCalibration -or $TongueArcCalibration -or $CheekStillCalibration -or -not [string]::IsNullOrWhiteSpace($RecordPath)
    $labelsEnabled = ($recordEnabled -or $ModelPreview -or $TonguePreview -or $EyeCalibration -or $HybridPreview) -and -not $NoLabels
    $vrcftRequired = $Calibration -or $TongueCalibration -or $TongueStillCalibration -or $TongueCorrectionCalibration -or $TongueRefinementCalibration -or $TongueArcCalibration -or $ModelPreview -or $TonguePreview -or $PupilOutput -or $EyeCalibration -or $HybridPreview
    if ($TrackingSource -eq "SteamLink" -and $labelsEnabled) {
        if ($LabelsPort -ne 27274) { throw "Steam Link training labels use port 27274; omit -LabelsPort or set it to 27274." }
        $vrcftRequired = $true
        Write-Host "Steam Link labels: the Qpro VRCFaceTracking module must receive OSC on port 9015 and publish factory reference packets to 127.0.0.1:$LabelsPort."
        Write-Host "On the headset, enable Steam Link Advanced Settings > OSC, Share eye tracking, Share face tracking, and set OSC Output Port to 9015."
    }
    if ($vrcftRequired) {
        $sourceDisplayName = if ($TrackingSource -eq "SteamLink") { "Steam Link" } else { "Virtual Desktop" }
        $moduleName = if ($TrackingSource -eq "SteamLink") { "000-Qpro.SteamLink.dll" } else { "000-Qpro.VirtualDesktop.dll" }
        $otherModuleName = if ($TrackingSource -eq "SteamLink") { "000-Qpro.VirtualDesktop.dll" } else { "000-Qpro.SteamLink.dll" }
        $customLibs = Join-Path $env:APPDATA "VRCFaceTracking\CustomLibs"
        $qproModule = Join-Path $customLibs $moduleName
        $packagedModule = Join-Path $PSScriptRoot "vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll"
        if (-not (Test-Path -LiteralPath $qproModule -PathType Leaf)) {
            throw "The Qpro $sourceDisplayName VRCFaceTracking module is not installed. Close VRCFaceTracking, use First-time setup > Install $sourceDisplayName module, then reopen VRCFaceTracking."
        }
        if ((Test-Path -LiteralPath (Join-Path $customLibs $otherModuleName)) -or
            (Test-Path -LiteralPath (Join-Path $customLibs "000-Qpro.IndependentGaze.dll"))) {
            throw "More than one Qpro VRCFaceTracking module is installed. Close VRCFaceTracking and reinstall the selected $sourceDisplayName module from First-time setup."
        }
        if (-not (Test-Path -LiteralPath $packagedModule -PathType Leaf) -or
            (Get-FileHash -LiteralPath $qproModule -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $packagedModule -Algorithm SHA256).Hash) {
            throw "The installed Qpro VRCFaceTracking module does not match this build. Close VRCFaceTracking and reinstall the $sourceDisplayName module from First-time setup."
        }
    }
    $steamVrRequired = $vrcftRequired -or $EyeCalibration
    $openSourceModelsRequired = $OpenSourcePreview -or $EyeCalibration -or $HybridPreview
    if ($RecordSeconds -gt 0 -and -not $recordEnabled) {
        throw "RecordSeconds requires -Record or -RecordPath."
    }
    if ($Calibration -and $CameraMode -ne "all") { throw "Calibration requires -CameraMode all (the default)." }
    if ($Calibration -and $NoWindow) { throw "Calibration requires the visible prompt window." }
    if ($Calibration -and $RecordSeconds -gt 0) { throw "Calibration controls its own duration; do not set RecordSeconds." }
    if ($Calibration -and $NoLabels) { throw "Calibration requires factory labels from the selected tracking source." }
    if ($TongueCalibration -and $NoWindow) { throw "TongueCalibration requires the visible prompt window." }
    if ($TongueCalibration -and $RecordSeconds -gt 0) { throw "TongueCalibration controls its own duration; do not set RecordSeconds." }
    if ($TongueCalibration -and $NoLabels) { throw "TongueCalibration requires the native TongueOut reference stream." }
    if ($TongueCalibration -and $Calibration) { throw "Run TongueCalibration separately from whole-face Calibration." }
    if ($TongueCalibration -and ($ModelPreview -or $OpenSourcePreview -or $HybridPreview -or $EyeCalibration)) {
        throw "Run TongueCalibration by itself."
    }
    if ($TongueStillCalibration -and $NoWindow) { throw "TongueStillCalibration requires the visible prompt window." }
    if ($TongueStillCalibration -and $RecordSeconds -gt 0) { throw "TongueStillCalibration controls its own capture; do not set RecordSeconds." }
    if ($TongueStillCalibration -and $NoLabels) { throw "TongueStillCalibration requires the native TongueOut reference stream." }
    if ($TongueStillCalibration -and ($Calibration -or $TongueCalibration -or $TongueCorrectionCalibration -or $TongueRefinementCalibration -or $TongueArcCalibration -or $ModelPreview -or $TonguePreview -or $OpenSourcePreview -or $HybridPreview -or $EyeCalibration)) {
        throw "Run TongueStillCalibration by itself."
    }
    if ($TongueCorrectionCalibration -and $NoWindow) { throw "TongueCorrectionCalibration requires the visible prompt window." }
    if ($TongueCorrectionCalibration -and $RecordSeconds -gt 0) { throw "TongueCorrectionCalibration controls its own capture; do not set RecordSeconds." }
    if ($TongueCorrectionCalibration -and $NoLabels) { throw "TongueCorrectionCalibration requires the native TongueOut reference stream." }
    if ($TongueCorrectionCalibration -and ($Calibration -or $TongueCalibration -or $TongueStillCalibration -or $TongueRefinementCalibration -or $TongueArcCalibration -or $ModelPreview -or $TonguePreview -or $OpenSourcePreview -or $HybridPreview -or $EyeCalibration)) {
        throw "Run TongueCorrectionCalibration by itself."
    }
    if ($TongueRefinementCalibration -and $NoWindow) { throw "TongueRefinementCalibration requires the visible prompt window." }
    if ($TongueRefinementCalibration -and $RecordSeconds -gt 0) { throw "TongueRefinementCalibration controls its own capture; do not set RecordSeconds." }
    if ($TongueRefinementCalibration -and $NoLabels) { throw "TongueRefinementCalibration requires the native TongueOut reference stream." }
    if ($TongueRefinementCalibration -and ($Calibration -or $TongueCalibration -or $TongueStillCalibration -or $TongueCorrectionCalibration -or $TongueArcCalibration -or $ModelPreview -or $TonguePreview -or $OpenSourcePreview -or $HybridPreview -or $EyeCalibration)) {
        throw "Run TongueRefinementCalibration by itself."
    }
    if ($TongueArcCalibration -and $NoWindow) { throw "TongueArcCalibration requires the visible prompt window." }
    if ($TongueArcCalibration -and $RecordSeconds -gt 0) { throw "TongueArcCalibration controls its own capture; do not set RecordSeconds." }
    if ($TongueArcCalibration -and $NoLabels) { throw "TongueArcCalibration requires the native TongueOut reference stream." }
    if ($TongueArcCalibration -and ($Calibration -or $TongueCalibration -or $TongueStillCalibration -or $TongueCorrectionCalibration -or $TongueRefinementCalibration -or $ModelPreview -or $TonguePreview -or $OpenSourcePreview -or $HybridPreview -or $EyeCalibration)) {
        throw "Run TongueArcCalibration by itself."
    }
    if ($CheekStillCalibration -and ($NoWindow -or $RecordSeconds -gt 0)) {
        throw "CheekStillCalibration requires the visible prompt window and controls its own capture."
    }
    if ($CheekStillCalibration -and ($Calibration -or $TongueCalibration -or $TongueStillCalibration -or $TongueCorrectionCalibration -or $TongueRefinementCalibration -or $TongueArcCalibration -or $TonguePreview -or $ModelPreview -or $PupilOutput -or $OpenSourcePreview -or $HybridPreview -or $EyeCalibration)) {
        throw "Run CheekStillCalibration by itself."
    }
    if ($EnableCheekOutput -and -not $TonguePreview) { throw "EnableCheekOutput requires the selected tongue-and-cheek camera model." }
    if ($ModelPreview -and $CameraMode -ne "all") { throw "ModelPreview requires -CameraMode all (the default)." }
    if ($ModelPreview -and $NoWindow) { throw "ModelPreview requires visible comparison windows." }
    if ($ModelPreview -and $NoLabels) { throw "ModelPreview requires factory labels from the selected tracking source." }
    if ($ModelPreview -and -not (Test-Path -LiteralPath $ModelPath)) { throw "Model checkpoint not found: $ModelPath" }
    if ($TonguePreview -and $NoLabels) { throw "TonguePreview requires native TongueOut confidence." }
    if ($TonguePreview -and -not (Test-Path -LiteralPath $TongueModelPath)) { throw "Tongue model not found: $TongueModelPath" }
    if ($TonguePreview -and -not [string]::IsNullOrWhiteSpace($TongueDirectionModelPath) -and -not (Test-Path -LiteralPath $TongueDirectionModelPath)) {
        throw "Tongue direction model not found: $TongueDirectionModelPath"
    }
    if ($TonguePreview -and ($Calibration -or $TongueCalibration -or $TongueStillCalibration -or $TongueCorrectionCalibration -or $TongueRefinementCalibration -or $TongueArcCalibration -or $ModelPreview -or $OpenSourcePreview -or $HybridPreview -or $EyeCalibration)) {
        throw "Run TonguePreview by itself."
    }
    if ($openSourceModelsRequired -and $CameraMode -ne "all") { throw "OpenSourcePreview and EyeCalibration require -CameraMode all (the default)." }
    if ($openSourceModelsRequired -and $NoWindow) { throw "OpenSourcePreview and EyeCalibration require visible preview windows." }
    if ($OpenSourcePreview -and $ModelPreview) { throw "Choose either -OpenSourcePreview or -ModelPreview." }
    if ($HybridPreview -and ($OpenSourcePreview -or $ModelPreview -or $Calibration -or $EyeCalibration)) {
        throw "Run HybridPreview by itself."
    }
    if ($OpenSourcePreview -and $Calibration) { throw "Run OpenSourcePreview separately from calibration." }
    if ($EyeCalibration -and $Calibration) { throw "Run EyeCalibration separately from whole-face Calibration." }
    if ($EyeCalibration -and $ModelPreview) { throw "Run EyeCalibration separately from ModelPreview." }
    if ($EyeCalibration -and $NoLabels) { throw "EyeCalibration requires the Meta/Virtual Desktop factory eye baseline." }
    if ($HybridPreview -and $NoLabels) { throw "HybridPreview requires live Meta/Virtual Desktop signals." }
    if ($HybridPreview -and -not (Test-Path -LiteralPath $HybridCalibrationPath)) { throw "Hybrid calibration not found: $HybridCalibrationPath" }
    if ($EyeCalibration -and -not (Test-Path -LiteralPath $CalibrationOverlayPath)) { throw "BabbleCalibration executable not found: $CalibrationOverlayPath" }
    if ($openSourceModelsRequired -and -not (Test-Path -LiteralPath $NextModelPath)) { throw "EyeTrackVR NEXT model not found: $NextModelPath" }
    if ($openSourceModelsRequired -and -not (Test-Path -LiteralPath $FaceModelPath)) { throw "Project Babble face model not found: $FaceModelPath" }
    if ($vrcftRequired -and -not (Get-Process -Name "VRCFaceTracking" -ErrorAction SilentlyContinue)) {
        throw "Start VRCFaceTracking first. No VRCFaceTracking process was found."
    }
    if ($steamVrRequired -and -not (Get-Process -Name "vrserver" -ErrorAction SilentlyContinue)) {
        throw "Start SteamVR first. SteamVR's vrserver process was not found."
    }

    $rocmPython = $null
    Remove-Item Env:QPRO_ROCM_INSTALL_SMOKE_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:QPRO_ROCM_EXPECTED_GFX_TARGET -ErrorAction SilentlyContinue
    if ($TonguePreview -or $PupilOutput) {
        $rocmCandidates = @(Get-QproRocmCandidates $PSScriptRoot)
        foreach ($candidate in $rocmCandidates) {
            if (-not (Test-Path -LiteralPath $candidate.Python -PathType Leaf) -or
                -not (Test-Path -LiteralPath $candidate.ReadyMarker -PathType Leaf)) { continue }
            # Inherited masks may hide the discrete card or renumber it. Let
            # Qpro validate names in HIP's actual order before choosing a GPU.
            # Keep this local to the ROCm run; restore before a CUDA fallback.
            foreach ($name in $qproGpuVisibilityNames) {
                [Environment]::SetEnvironmentVariable($name, $null, 'Process')
            }
            if ($candidate.TargetFamily) { $env:ROCM_SDK_TARGET_FAMILY = $candidate.TargetFamily }
            else { Remove-Item Env:ROCM_SDK_TARGET_FAMILY -ErrorAction SilentlyContinue }
            $probeSucceeded = $false
            try {
                $global:LASTEXITCODE = $null
                & $candidate.Python -c "import torch; from qpro_gpu import require_rocm_device_name; d=require_rocm_device_name(torch); print('Tracking GPU:', torch.cuda.get_device_name(int(d.split(':')[1])), 'on', d)"
                $probeSucceeded = $LASTEXITCODE -eq 0
            } catch { Write-Warning "$($candidate.Name) validation failed: $_" }
            if ($probeSucceeded) {
                $rocmPython = $candidate.Python
                Write-Host "Selected $($candidate.Name) runtime."
                break
            }
            Write-Warning "$($candidate.Name) cannot see a supported discrete Radeon GPU. Trying the next runtime."
        }
    }
    if (-not $rocmPython) {
        Restore-QproGpuVisibility
        Remove-Item Env:ROCM_SDK_TARGET_FAMILY -ErrorAction SilentlyContinue
    }
    $python = if ($rocmPython) { $rocmPython } elseif (-not [string]::IsNullOrWhiteSpace($env:QPRO_PYTHON)) { $env:QPRO_PYTHON } else { Join-Path $PSScriptRoot ".venv\Scripts\python.exe" }
    Write-Host "PC Python runtime: $python"
    $pythonFallback = Join-Path $PSScriptRoot ".venv\Scripts\qpro-python-console.exe"
    if (-not (Test-Path -LiteralPath $python) -and (Test-Path -LiteralPath $pythonFallback)) {
        $python = $pythonFallback
    }
    if (-not (Test-Path $python)) {
        if ($SkipPythonSetup) { throw "Python environment missing. Run again without -SkipPythonSetup." }
        & py -3 -m venv .\.venv
        if ($LASTEXITCODE -ne 0) { throw "Creating the Python environment failed." }
        $python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
    }
    & $python -c "import cv2, numpy; assert hasattr(cv2, 'namedWindow') and hasattr(cv2, 'destroyAllWindows'), 'OpenCV GUI support is missing'" 2>$null
    if ($LASTEXITCODE -ne 0) {
        if ($SkipPythonSetup) { throw "OpenCV with Windows GUI support or NumPy is missing. Run again without -SkipPythonSetup." }
        & $python -m pip uninstall --yes opencv-python-headless opencv-contrib-python-headless
        & $python -m pip install --disable-pip-version-check --upgrade --force-reinstall opencv-python numpy
        if ($LASTEXITCODE -ne 0) { throw "Installing the PC preview dependencies failed." }
        & $python -c "import cv2, numpy; assert hasattr(cv2, 'namedWindow') and hasattr(cv2, 'destroyAllWindows'), 'OpenCV GUI support is missing'"
        if ($LASTEXITCODE -ne 0) { throw "OpenCV installed, but Windows GUI support is still unavailable." }
    }
    if ($openSourceModelsRequired) {
        & $python -c "import onnxruntime as ort; assert hasattr(ort, 'InferenceSession'), 'ONNX Runtime is incomplete'" 2>$null
        if ($LASTEXITCODE -ne 0) {
            if ($SkipPythonSetup) { throw "ONNX Runtime is missing. Run again without -SkipPythonSetup." }
            $sitePackages = Join-Path $PSScriptRoot ".venv\Lib\site-packages"
            & py -3 -m pip install --disable-pip-version-check --upgrade --target $sitePackages onnxruntime
            if ($LASTEXITCODE -ne 0) { throw "Installing ONNX Runtime for the open-source preview failed." }
            & $python -c "import onnxruntime as ort; assert hasattr(ort, 'InferenceSession'), 'ONNX Runtime is incomplete'"
            if ($LASTEXITCODE -ne 0) { throw "ONNX Runtime installed, but its inference API is unavailable." }
        }
    }

    if ($labelsEnabled -and $TrackingSource -eq "VirtualDesktop") {
        $labelBridgeExe = Join-Path $PSScriptRoot "vd-label-bridge\bin\Release\net10.0\Qpro.VirtualDesktopLabelBridge.exe"
        if ($RebuildManaged -or -not (Test-Path -LiteralPath $labelBridgeExe)) {
            $labelBridgeProject = Join-Path $PSScriptRoot "vd-label-bridge\Qpro.VirtualDesktopLabelBridge.csproj"
            if (-not (Test-Path -LiteralPath $labelBridgeProject)) {
                throw "The prebuilt Virtual Desktop label bridge is missing. Reinstall the release package."
            }
            & dotnet build $labelBridgeProject -c Release
            if ($LASTEXITCODE -ne 0) { throw "Building the Virtual Desktop label bridge failed." }
        }
        if (-not (Test-Path -LiteralPath $labelBridgeExe)) { throw "The Virtual Desktop label bridge executable was not produced." }
        $labelBridgeExe = Get-ShortLabelBridgePath $labelBridgeExe
    }

    if ($needsNativeBuild) {
        & $clang --target=aarch64-linux-android28 -std=c11 -O3 -Wall -Wextra -fPIC -shared "-Wl,-z,max-page-size=16384" .\streamer.c -o .\libquestpro-camera-streamer-v8.so
        if ($LASTEXITCODE -ne 0) { throw "Streamer compilation failed with exit code $LASTEXITCODE" }
        & $clang --target=aarch64-linux-android28 -std=c11 -O3 -Wall -Wextra -fPIE -pie "-DSTREAM_PORT=$StreamPort" "-Wl,-z,max-page-size=16384" .\relay.c -o .\questpro-camera-relay-v8
        if ($LASTEXITCODE -ne 0) { throw "Relay compilation failed with exit code $LASTEXITCODE" }
        & $clang --target=aarch64-linux-android28 -std=c11 -O2 -Wall -Wextra -fPIE -pie "-Wl,-z,max-page-size=16384" .\injector.c -o .\questpro-camera-injector -ldl
        if ($LASTEXITCODE -ne 0) { throw "Injector compilation failed with exit code $LASTEXITCODE" }
    }
    else {
        Write-Host "Using packaged Quest Pro headset binaries."
    }

    & $adbExecutable push .\libquestpro-camera-streamer-v8.so /data/local/tmp/libquestpro-camera-streamer-v8.so
    if ($LASTEXITCODE -ne 0) { throw "Pushing the streamer failed with exit code $LASTEXITCODE" }
    & $adbExecutable push .\questpro-camera-relay-v8 /data/local/tmp/questpro-camera-relay-v8
    if ($LASTEXITCODE -ne 0) { throw "Pushing the relay failed with exit code $LASTEXITCODE" }
    & $adbExecutable push .\questpro-camera-injector /data/local/tmp/questpro-camera-injector
    if ($LASTEXITCODE -ne 0) { throw "Pushing the injector failed with exit code $LASTEXITCODE" }

    # adb push creates these files as Android Shell. This Magisk build grants
    # uid 0 without CAP_FOWNER, so a later `su -c chmod` cannot change a
    # shell-owned file. Set modes as their actual owner before entering su.
    & $adbExecutable shell chmod 755 /data/local/tmp/questpro-camera-relay-v8 /data/local/tmp/questpro-camera-injector
    if ($LASTEXITCODE -ne 0) { throw "Marking the headset executables runnable failed with exit code $LASTEXITCODE" }
    & $adbExecutable shell chmod 644 /data/local/tmp/libquestpro-camera-streamer-v8.so
    if ($LASTEXITCODE -ne 0) { throw "Setting the streamer library permissions failed with exit code $LASTEXITCODE" }
    & $adbExecutable shell "rm -f /data/local/tmp/questpro-live-v3.log /data/local/tmp/questpro-live-v8.log; touch /data/local/tmp/questpro-live-v3.log /data/local/tmp/questpro-live-v8.log; chmod 666 /data/local/tmp/questpro-live-v3.log /data/local/tmp/questpro-live-v8.log"
    if ($LASTEXITCODE -ne 0) { throw "Preparing the headset logs as Android Shell failed with exit code $LASTEXITCODE" }

    # The relay safely replaces older Quest Pro relay processes itself. Keeping
    # this out of a nested adb/su shell avoids Windows quoting failures.
    $null = & $adbExecutable forward --remove "tcp:$StreamPort" 2>&1
    $injectCommand = "/data/local/tmp/questpro-camera-injector /data/local/tmp/libquestpro-camera-streamer-v8.so"
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        # This Magisk build exposes incomplete provider mappings to a second su
        # session while the long-lived wireless relay session is open. Inject
        # first; the streamer intentionally waits for the relay's shared file.
        $injectOutput = & $adbExecutable shell su -c $injectCommand 2>&1
        $injectExit = $LASTEXITCODE
        @($injectOutput | ForEach-Object { $_.ToString() }) | Tee-Object -FilePath .\questpro-live-inject.txt
        if ($injectExit -ne 0) { throw "Injection failed with exit code $injectExit. Send questpro-live-inject.txt." }
    }
    if (-not [string]::IsNullOrWhiteSpace($AdbTarget)) {
        # A relay orphaned by `nohup ... &` loses the live Magisk su execution
        # context on this headset and then cannot fchmod/mmap the shared file.
        # Keep the adb/su session alive for the relay's lifetime over Wi-Fi.
        $relayExec = "/data/local/tmp/questpro-camera-relay-v8 --mode $CameraMode --max-fps $MaxFps"
        Remove-Item -LiteralPath $relayClientOutputPath, $relayClientErrorPath -Force -ErrorAction SilentlyContinue
        $relayArguments = "shell su -c `"$relayExec`""
        $relayProcess = Start-Process -FilePath $adbExecutable `
            -ArgumentList $relayArguments `
            -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $relayClientOutputPath `
            -RedirectStandardError $relayClientErrorPath
        Start-Sleep -Milliseconds 800
        if ($relayProcess.HasExited) {
            $relayOutput = if (Test-Path -LiteralPath $relayClientOutputPath) { Get-Content -LiteralPath $relayClientOutputPath -Raw } else { "" }
            $relayError = if (Test-Path -LiteralPath $relayClientErrorPath) { Get-Content -LiteralPath $relayClientErrorPath -Raw } else { "" }
            $relayLogText = (($relayOutput, $relayError) -join "`n").Trim()
            Set-Content -LiteralPath .\questpro-live-relay.txt -Value $relayLogText
            if (-not [string]::IsNullOrWhiteSpace($relayLogText)) { Write-Host $relayLogText }
            $relayProcess.Dispose()
            $relayProcess = $null
            throw "The wireless root relay exited during startup. Send questpro-live-relay.txt."
        }
        $relayStarted = $true
        Write-Host "RELAY_LISTENING address=127.0.0.1 port=$StreamPort mode=$CameraMode max_fps=$MaxFps transport=live-adb-su"
    }
    else {
        $relayCommand = 'chmod 755 /data/local/tmp/questpro-camera-relay-v8; : > /data/local/tmp/questpro-relay-v8.log; nohup /data/local/tmp/questpro-camera-relay-v8 --mode {0} --max-fps {1} > /data/local/tmp/questpro-relay-v8.log 2>&1 < /dev/null &' -f $CameraMode, $MaxFps
        & $adbExecutable shell su -c $relayCommand
        if ($LASTEXITCODE -ne 0) { throw "Starting the root relay failed with exit code $LASTEXITCODE" }
        $relayStarted = $true
        Start-Sleep -Milliseconds 800
        $relayLog = & $adbExecutable shell su -c "cat /data/local/tmp/questpro-relay-v8.log" 2>&1
        @($relayLog | ForEach-Object { $_.ToString() }) | Tee-Object -FilePath .\questpro-live-relay.txt
        if (-not (($relayLog -join "`n") -match "RELAY_LISTENING")) { throw "The root relay did not begin listening. Send questpro-live-relay.txt." }
    }

    if ([string]::IsNullOrWhiteSpace($AdbTarget)) {
        $injectOutput = & $adbExecutable shell su -c $injectCommand 2>&1
        $injectExit = $LASTEXITCODE
        @($injectOutput | ForEach-Object { $_.ToString() }) | Tee-Object -FilePath .\questpro-live-inject.txt
        if ($injectExit -ne 0) { throw "Injection failed with exit code $injectExit. Send questpro-live-inject.txt." }
    }

    Start-Sleep -Milliseconds 800
    $headsetLog = & $adbExecutable shell su -c "cat /data/local/tmp/questpro-live-v8.log" 2>&1
    @($headsetLog | ForEach-Object { $_.ToString() }) | Tee-Object -FilePath .\questpro-live-headset.txt

    & $adbExecutable forward "tcp:$StreamPort" "tcp:$StreamPort"
    if ($LASTEXITCODE -ne 0) { throw "ADB port forwarding failed with exit code $LASTEXITCODE" }

    $capText = if ($MaxFps -eq 0) { "unlimited" } else { "$MaxFps FPS" }
    Write-Host "Transport mode: $CameraMode; cap: $capText"
    if ($labelsEnabled -and $TrackingSource -eq "VirtualDesktop") {
        $labelLogRoot = Join-Path $env:LOCALAPPDATA 'QproFaceTracking\runtime\logs'
        New-Item -ItemType Directory -Force -Path $labelLogRoot | Out-Null
        $labelOutputPath = Join-Path $labelLogRoot "label-bridge-$PID-output.txt"
        $labelErrorPath = Join-Path $labelLogRoot "label-bridge-$PID-error.txt"
        $labelBridgeProcess = Start-Process -FilePath $labelBridgeExe -ArgumentList @("--port", "$LabelsPort") -PassThru -WindowStyle Hidden -WorkingDirectory (Split-Path -Parent $labelBridgeExe) -RedirectStandardOutput $labelOutputPath -RedirectStandardError $labelErrorPath
        Start-Sleep -Milliseconds 300
        if ($labelBridgeProcess.HasExited) {
            throw "The Virtual Desktop label bridge exited during startup. Send $labelErrorPath."
        }
    }
    $receiverArguments = @(".\receiver.py", "--port", "$StreamPort")
    if ($CompanionPid -gt 0) {
        $receiverArguments += @("--companion-pid", "$CompanionPid", "--companion-start-filetime", "$CompanionStartFileTime")
    }
    if ($NoWindow) { $receiverArguments += "--no-window" }
    if ($recordEnabled) {
        $receiverArguments += "--record"
        if (-not [string]::IsNullOrWhiteSpace($RecordPath)) { $receiverArguments += $RecordPath }
        if ($RecordSeconds -gt 0) { $receiverArguments += @("--record-seconds", "$RecordSeconds") }
        $receiverArguments += @("--labels-port", "$LabelsPort")
        if ($NoLabels) { $receiverArguments += "--no-labels" }
        if ($Calibration) { $receiverArguments += "--calibration" }
        if ($TongueCalibration) { $receiverArguments += "--tongue-calibration" }
        if ($TongueStillCalibration) { $receiverArguments += "--tongue-still-calibration" }
        if ($TongueCorrectionCalibration) { $receiverArguments += "--tongue-correction-calibration" }
        if ($TongueRefinementCalibration) { $receiverArguments += "--tongue-refinement-calibration" }
        if ($TongueArcCalibration) { $receiverArguments += "--tongue-arc-calibration" }
        if ($CheekStillCalibration) { $receiverArguments += "--cheek-still-calibration" }
    }
    if ($ModelPreview) {
        $resolvedModelPath = (Resolve-Path -LiteralPath $ModelPath).Path
        $receiverArguments += @("--model", $resolvedModelPath, "--model-device", $ModelDevice, "--labels-port", "$LabelsPort")
    }
    if ($TonguePreview) {
        $resolvedTongueModelPath = (Resolve-Path -LiteralPath $TongueModelPath).Path
        $receiverArguments += @(
            "--tongue-model", $resolvedTongueModelPath,
            "--tongue-model-device", $TongueModelDevice,
            "--tongue-smoothing", "$TongueSmoothing",
            "--tongue-visibility-mode", $TongueVisibilityMode,
            "--labels-port", "$LabelsPort"
        )
        if (-not [string]::IsNullOrWhiteSpace($TongueDirectionModelPath)) {
            $resolvedTongueDirectionModelPath = (Resolve-Path -LiteralPath $TongueDirectionModelPath).Path
            $receiverArguments += @("--tongue-direction-model", $resolvedTongueDirectionModelPath)
        }
        if ($EnableTongueOutput) { $receiverArguments += "--tongue-output" }
        if ($EnableCheekOutput) { $receiverArguments += "--cheek-output" }
    }
    if ($PupilOutput) {
        $receiverArguments += @("--pupil-output", "--pupil-sensitivity", $PupilSensitivity.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture))
    }
    if (-not [string]::IsNullOrWhiteSpace($StopFile)) {
        $receiverArguments += @("--stop-file", (Resolve-WorkspacePath $StopFile))
    }
    if ($openSourceModelsRequired) {
        $resolvedNextModelPath = (Resolve-Path -LiteralPath $NextModelPath).Path
        $resolvedFaceModelPath = (Resolve-Path -LiteralPath $FaceModelPath).Path
        $previewSwitch = if ($HybridPreview) { "--hybrid-preview" } else { "--open-source-preview" }
        $receiverArguments += @(
            $previewSwitch,
            "--next-model", $resolvedNextModelPath,
            "--face-model", $resolvedFaceModelPath
        )
        if ($HybridPreview) {
            $resolvedHybridCalibrationPath = (Resolve-Path -LiteralPath $HybridCalibrationPath).Path
            $receiverArguments += @(
                "--hybrid-calibration", $resolvedHybridCalibrationPath,
                "--labels-port", "$LabelsPort"
            )
        }
    }
    if ($EyeCalibration) {
        $resolvedCalibrationOverlayPath = (Resolve-Path -LiteralPath $CalibrationOverlayPath).Path
        $receiverArguments += @(
            "--eye-calibration",
            "--calibration-overlay", $resolvedCalibrationOverlayPath,
            "--eye-calibration-output", $EyeCalibrationOutput,
            "--labels-port", "$LabelsPort",
            "--gaze-calibration-seconds", "$GazeCalibrationSeconds",
            "--convergence-calibration-seconds", "$ConvergenceCalibrationSeconds"
        )
    }
    & $python @receiverArguments
    if ($LASTEXITCODE -ne 0) { throw "The PC tracking runtime exited with code $LASTEXITCODE." }
} catch {
    $launcherFailure = $_
} finally {
    if ($null -ne $labelBridgeProcess -and -not $labelBridgeProcess.HasExited) {
        Stop-Process -Id $labelBridgeProcess.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $labelBridgeProcess.Id -Timeout 2 -ErrorAction SilentlyContinue
    }
    if ($relayStarted) {
        $stopOutput = & $adbExecutable shell su -c "/data/local/tmp/questpro-camera-relay-v8 --stop" 2>&1
        if ($LASTEXITCODE -eq 0) { $stopOutput | ForEach-Object { Write-Host $_ } }
        if ($null -ne $relayProcess) {
            if (-not $relayProcess.WaitForExit(2000)) {
                $relayProcess.Kill()
                $relayProcess.WaitForExit(2000) | Out-Null
            }
            $relayOutput = if (Test-Path -LiteralPath $relayClientOutputPath) { Get-Content -LiteralPath $relayClientOutputPath -Raw } else { "" }
            $relayError = if (Test-Path -LiteralPath $relayClientErrorPath) { Get-Content -LiteralPath $relayClientErrorPath -Raw } else { "" }
            (($relayOutput, $relayError) -join "`n").Trim() | Set-Content -LiteralPath .\questpro-live-relay.txt
            $relayProcess.Dispose()
        }
        else {
            $finalRelayLog = & $adbExecutable shell su -c "cat /data/local/tmp/questpro-relay-v8.log" 2>&1
            @($finalRelayLog | ForEach-Object { $_.ToString() }) | Set-Content -LiteralPath .\questpro-live-relay.txt
        }
    }
    $null = & $adbExecutable forward --remove "tcp:$StreamPort" 2>&1
    Restore-QproGpuVisibility
    Pop-Location
    if ($hadAndroidSerial) {
        $env:ANDROID_SERIAL = $previousAndroidSerial
    } else {
        Remove-Item Env:ANDROID_SERIAL -ErrorAction SilentlyContinue
    }
}
if ($null -ne $launcherFailure) {
    $ErrorActionPreference = "Stop"
    throw $launcherFailure
}
