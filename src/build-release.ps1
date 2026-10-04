param(
    [string]$Version = "2.1.2",
    [string]$PackageName = "",
    [switch]$NoRestore,
    [string]$AssetRoot = "",
    [string]$VrcftInstallDir = "",
    [string]$ExperimentalTongueModelRoot = "",
    [string]$CameraCheekModelRoot = "",
    [string]$TestNotesPath = "",
    [string]$ControllerInputAssetRoot = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$safeVersion = $Version -replace '[^A-Za-z0-9._-]', '-'
$releaseName = if ([string]::IsNullOrWhiteSpace($PackageName)) { "QproFaceTracking-$safeVersion" } else { $PackageName }
if ($releaseName -match '[\\/:*?"<>|]' -or $releaseName -in @(".", "..") -or $releaseName.EndsWith(" ") -or $releaseName.EndsWith(".")) {
    throw "Invalid package folder name: $releaseName"
}
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root "dist"))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $distRoot $releaseName))
$runtimeRoot = Join-Path $releaseRoot "QproRuntime"
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts\release-$safeVersion"))
$pythonArchiveUrl = 'https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg'
$pythonArchiveSha256 = '0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8'
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT "dotnet.exe") -PathType Leaf)) {
    Join-Path $env:DOTNET_ROOT "dotnet.exe"
} else { "dotnet" }
$restoreArgs = @(if ($NoRestore) { "--no-restore" })
$assetRootResolved = if ([string]::IsNullOrWhiteSpace($AssetRoot)) { "" } else { [System.IO.Path]::GetFullPath($AssetRoot) }
if ($assetRootResolved -and -not (Test-Path -LiteralPath $assetRootResolved -PathType Container)) {
    throw "Asset root does not exist: $assetRootResolved"
}

if (-not $releaseRoot.StartsWith($distRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe release target: $releaseRoot"
}
$artifactsParent = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
if (-not $artifactRoot.StartsWith($artifactsParent + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe build artifact target: $artifactRoot"
}
if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releaseRoot, $artifactRoot | Out-Null

Write-Host "Publishing the self-contained Windows hub..."
$hubPublish = Join-Path $artifactRoot "hub"
& $dotnet publish (Join-Path $root "qpro-hub\QproFaceTracking.Hub.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $hubPublish @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "Publishing the Windows hub failed." }
Copy-Item -LiteralPath (Join-Path $hubPublish "QproFaceTracking.Hub.exe") -Destination (Join-Path $releaseRoot "QproFaceTracking.exe")

Write-Host "Publishing the self-contained label bridge..."
$labelPublish = Join-Path $artifactRoot "label-bridge"
& $dotnet publish (Join-Path $root "vd-label-bridge\Qpro.VirtualDesktopLabelBridge.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $labelPublish @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "Publishing the label bridge failed." }
$labelDestination = Join-Path $runtimeRoot "vd-label-bridge\bin\Release\net10.0"
New-Item -ItemType Directory -Force -Path $labelDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $labelPublish "Qpro.VirtualDesktopLabelBridge.exe") -Destination $labelDestination

Write-Host "Building the combined VRCFT bridge..."
$vrcftBuildArgs = @(if (-not [string]::IsNullOrWhiteSpace($VrcftInstallDir)) {
    "-p:VrcftInstallDir=$([System.IO.Path]::GetFullPath($VrcftInstallDir))"
})
& $dotnet build (Join-Path $root "vrcft-gaze-bridge\Qpro.GazeBridge.csproj") -c Release -p:DebugType=None @vrcftBuildArgs @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "Building the combined VRCFT bridge failed." }
$vrcftBinaryDestination = Join-Path $runtimeRoot "vrcft-gaze-bridge\bin\Release\net10.0"
New-Item -ItemType Directory -Force -Path $vrcftBinaryDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $root "vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll") -Destination $vrcftBinaryDestination

function Get-QproPythonArchive {
    $cacheRoot = Join-Path $root 'artifacts\python-package'
    $archivePath = Join-Path $cacheRoot 'python.3.12.10.nupkg'
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        $cachedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($cachedHash -eq $pythonArchiveSha256) { return $archivePath }
        Write-Warning 'The cached Python NuGet archive failed SHA-256 verification; downloading a clean copy.'
    }

    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
    $temporaryPath = Join-Path $cacheRoot ('python.3.12.10-' + [guid]::NewGuid().ToString('N') + '.download')
    try {
        Write-Host 'Downloading the pinned Python 3.12.10 NuGet archive from nuget.org...'
        try {
            Invoke-WebRequest -Uri $pythonArchiveUrl -OutFile $temporaryPath -UseBasicParsing -TimeoutSec 180 -ErrorAction Stop
        }
        catch {
            throw "The required Python NuGet archive is missing and its official download failed: $($_.Exception.Message). Provide python-runtime\python.3.12.10.nupkg through -AssetRoot or retry with internet access."
        }
        if (-not (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
            throw 'The official Python NuGet download did not create an archive. Provide python-runtime\python.3.12.10.nupkg through -AssetRoot or retry.'
        }
        $downloadHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($downloadHash -ne $pythonArchiveSha256) {
            throw "The official Python NuGet download failed SHA-256 verification (expected $pythonArchiveSha256, received $downloadHash). No archive was cached."
        }
        if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
            $cachedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($cachedHash -eq $pythonArchiveSha256) { return $archivePath }
            Remove-Item -LiteralPath $archivePath -Force
        }
        Move-Item -LiteralPath $temporaryPath -Destination $archivePath
        return $archivePath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Copy-ReleaseFile([string]$RelativePath) {
    $source = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $source) -and $assetRootResolved) {
        $source = Join-Path $assetRootResolved $RelativePath
    }
    if (-not (Test-Path -LiteralPath $source) -and $RelativePath -eq 'python-runtime\python.3.12.10.nupkg') {
        $source = Get-QproPythonArchive
    }
    if (-not (Test-Path -LiteralPath $source)) { throw "Required release file is missing: $RelativePath" }
    $destination = Join-Path $runtimeRoot $RelativePath
    $parent = Split-Path -Parent $destination
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

$runtimeFiles = @(
    "build-and-run.ps1",
    "Connect-QproWireless.ps1",
    "Pair-QproWireless.ps1",
    "Launch-QproWireless.ps1",
    "Enable-QproWireless.ps1",
    "Disable-QproWireless.ps1",
    "Install-QproRocm.ps1",
    "preview-latest-tongue.ps1",
    "native-eye-local-branch-test.ps1",
    "install-vrcft-eye-bridge.ps1",
    "uninstall-vrcft-eye-bridge.ps1",
    "setup-runtime.ps1",
    "runtime-python.ps1",
    "prepare-eye-model.ps1",
    "prepare_eye_model.py",
    "qpro_eye_engines.py",
    "eye_detector_guard.py",
    "train-latest-tongue-stills.ps1",
    "train-latest-tongue-refinement.ps1",
    "requirements-runtime.txt",
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
    "pupil_gaze_calibration.py",
    "capture_format.py",
    "calibration.py",
    "tongue_calibration.py",
    "tongue_still_capture.py",
    "label_capture.py",
    "tongue_model_preview.py",
    "tongue_image_processing.py",
    "model_preview.py",
    "hybrid_preview.py",
    "train_tongue_model.py",
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
    "visual_axis_calibration.py",
    "stereo_eye_calibration.py",
    "libquestpro-camera-streamer-v8.so",
    "questpro-camera-relay-v8",
    "questpro-camera-injector",
    "calibration\qpro-independent-visual-axis-v2.json",
    "models\qpro-stereo-tongue-v8-gate.pt",
    "models\qpro-stereo-tongue-v8-direction.pt",
    "research\patch_seacliff_independent_axes.py",
    "release-manifest.json"
)
foreach ($file in $runtimeFiles) { Copy-ReleaseFile $file }

# The optional controller runtime is built separately from the .NET face module.
# Copy an explicit list so compiler caches, debug symbols and reference source
# cannot enter the runnable archive.
$controllerAssetRoot = if ($ControllerInputAssetRoot) {
    [System.IO.Path]::GetFullPath($ControllerInputAssetRoot)
} else { Join-Path $root 'artifacts\controller-native-build' }
$controllerSourceFiles = @(
    'controller-input.ps1', 'controller-input\manage.py',
    'hybrid\controller.py', 'hybrid\compatibility.json',
    'hybrid\quest_hand_adapter.js', 'hybrid\quest_controller_adapter.js',
    'hybrid\steamvr_skeleton_adapter.js',
    'controller-input\third_party\openvr\LICENSE'
)
foreach ($file in $controllerSourceFiles) { Copy-ReleaseFile $file; $runtimeFiles += $file }
$controllerAssets = @{
    'qpro-controller-input' = 'controller-input\qpro-controller-input'
    'qpro_controller\driver.vrdrivermanifest' = 'controller-input\addon\driver.vrdrivermanifest'
    'qpro_controller\bin\win64\driver_qpro_controller.dll' = 'controller-input\addon\bin\win64\driver_qpro_controller.dll'
    'qpro_controller\resources\settings.json' = 'controller-input\addon\resources\settings.json'
    'qpro_controller\resources\input\quest_pro_touchpad.json' = 'controller-input\addon\resources\input\quest_pro_touchpad.json'
    'qpro_controller\LICENSE.OpenVR.txt' = 'controller-input\addon\LICENSE.OpenVR.txt'
    'qpro_controller\README.md' = 'controller-input\addon\README.md'
}
foreach ($entry in $controllerAssets.GetEnumerator()) {
    $source = Join-Path $controllerAssetRoot $entry.Key
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Optional controller binary/resource is missing: $($entry.Key). Run controller-input\build-native.ps1 with a portable Zig compiler, or supply -ControllerInputAssetRoot."
    }
    $destination = Join-Path $runtimeRoot $entry.Value
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
    $runtimeFiles += $entry.Value
}

if (-not [string]::IsNullOrWhiteSpace($ExperimentalTongueModelRoot)) {
    $experimentalRoot = [System.IO.Path]::GetFullPath($ExperimentalTongueModelRoot)
    $metadataFiles = @(Get-ChildItem -LiteralPath $experimentalRoot -File -Filter 'qpro-stereo-tongue-v*.metadata.json')
    if ($metadataFiles.Count -ne 1) { throw 'The experimental model folder must identify exactly one paired model.' }
    $metadataFile = $metadataFiles[0]
    $metadata = Get-Content -LiteralPath $metadataFile.FullName -Raw | ConvertFrom-Json
    if ($metadataFile.Name -notmatch '^qpro-stereo-tongue-v(?<version>\d+)\.metadata\.json$') { throw 'Invalid experimental model filename.' }
    $experimentalVersion = [int]$Matches.version
    if ($experimentalVersion -le 8 -or $metadata.version -ne $experimentalVersion -or
        $metadata.format -ne 'qpro-tongue-model-metadata-v1' -or
        $metadata.modelKind -ne 'mustachio-experimental' -or $metadata.isExperimental -ne $true) {
        throw 'Experimental models require explicit Mustachio metadata and a separate version above v8.'
    }
    # Copy only the public weights and classification, never the private cache,
    # photographs, source journals or training logs beside them.
    foreach ($name in @("qpro-stereo-tongue-v$experimentalVersion-gate.pt", "qpro-stereo-tongue-v$experimentalVersion-direction.pt", $metadataFile.Name)) {
        $source = Join-Path $experimentalRoot $name
        $destination = Join-Path $runtimeRoot ("models\" + $name)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) { throw "Experimental model file is missing or empty: $name" }
        if (Test-Path -LiteralPath $destination) { throw "Experimental model would replace an existing model: $name" }
        Copy-Item -LiteralPath $source -Destination $destination
        $runtimeFiles += "models\$name"
    }
}
if (-not [string]::IsNullOrWhiteSpace($CameraCheekModelRoot)) {
    $cameraModelRoot = [System.IO.Path]::GetFullPath($CameraCheekModelRoot)
    $cameraMetadataFiles = @(Get-ChildItem -LiteralPath $cameraModelRoot -File -Filter 'qpro-stereo-tongue-v*.metadata.json')
    if ($cameraMetadataFiles.Count -ne 1) { throw 'The camera cheek model folder must identify exactly one paired model.' }
    $cameraMetadataFile = $cameraMetadataFiles[0]
    $cameraMetadata = Get-Content -LiteralPath $cameraMetadataFile.FullName -Raw | ConvertFrom-Json
    if ($cameraMetadataFile.Name -notmatch '^qpro-stereo-tongue-v(?<version>\d+)\.metadata\.json$') { throw 'Invalid camera cheek model filename.' }
    $cameraVersion = [int]$Matches.version
    if ($cameraVersion -le 8 -or $cameraMetadata.version -ne $cameraVersion -or
        $cameraMetadata.format -ne 'qpro-tongue-model-metadata-v1' -or
        $cameraMetadata.modelKind -ne 'camera-cheeks-experimental' -or
        $cameraMetadata.isExperimental -ne $true -or $cameraMetadata.hasCameraCheeks -ne $true -or
        $cameraMetadata.cheekTraining.frozenTongueParent -ne $true) {
        throw 'Camera cheek models require explicit experimental metadata and a frozen tongue parent.'
    }
    # This bundled candidate extends the developer v8 direction model. Keep its
    # original visibility gate, and include only sanitized public model assets.
    $cameraGate = Join-Path $cameraModelRoot "qpro-stereo-tongue-v$cameraVersion-gate.pt"
    $developerGate = Join-Path $runtimeRoot 'models\qpro-stereo-tongue-v8-gate.pt'
    if (-not (Test-Path -LiteralPath $cameraGate -PathType Leaf) -or
        (Get-FileHash -LiteralPath $cameraGate -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $developerGate -Algorithm SHA256).Hash) {
        throw 'The bundled camera cheek candidate must preserve the developer v8 visibility gate.'
    }
    foreach ($name in @("qpro-stereo-tongue-v$cameraVersion-gate.pt", "qpro-stereo-tongue-v$cameraVersion-direction.pt", $cameraMetadataFile.Name)) {
        $source = Join-Path $cameraModelRoot $name
        $destination = Join-Path $runtimeRoot ("models\" + $name)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) { throw "Camera cheek model file is missing or empty: $name" }
        if (Test-Path -LiteralPath $destination) { throw "Camera cheek model would replace an existing model: $name" }
        Copy-Item -LiteralPath $source -Destination $destination
        $runtimeFiles += "models\$name"
    }
}
# Check the packaged Python tree, not only the source list. A missing local
# module can otherwise leave capture working while training fails at import.
$missingLocalImports = @(
    Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File -Filter '*.py' | ForEach-Object {
        $importer = $_.FullName
        $code = [System.IO.File]::ReadAllText($importer)
        foreach ($match in [regex]::Matches($code, '(?m)^[ \t]*(?:from|import)[ \t]+([A-Za-z_][A-Za-z0-9_]*)')) {
            $module = $match.Groups[1].Value
            if ((Test-Path -LiteralPath (Join-Path $root "$module.py") -PathType Leaf) -and
                -not (Test-Path -LiteralPath (Join-Path $runtimeRoot "$module.py") -PathType Leaf)) {
                "$([System.IO.Path]::GetFileName($importer)) imports missing $module.py"
            }
        }
    } | Sort-Object -Unique
)
if ($missingLocalImports.Count) {
    throw "Local Python imports are missing from the release: $($missingLocalImports -join '; ')"
}
foreach ($file in @("succeed.wav", "trainingComplete.wav", "warning.wav")) {
    Copy-ReleaseFile ("SFX\" + $file)
}
foreach ($file in @("adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "NOTICE.txt", "source.properties")) {
    Copy-ReleaseFile ("platform-tools\" + $file)
}
foreach ($file in @("python.3.12.10.nupkg", "README.txt")) {
    Copy-ReleaseFile ("python-runtime\" + $file)
}
$pythonArchivePath = Join-Path $runtimeRoot 'python-runtime\python.3.12.10.nupkg'
$pythonArchiveHash = (Get-FileHash -LiteralPath $pythonArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($pythonArchiveHash -ne $pythonArchiveSha256) {
    throw 'The bundled Python NuGet archive failed its release integrity check.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pythonArchive = [System.IO.Compression.ZipFile]::OpenRead($pythonArchivePath)
try {
    $pythonLicense = $pythonArchive.GetEntry('tools/LICENSE.txt')
    if ($null -eq $pythonLicense) { throw 'The Python archive is missing its license.' }
    $pythonLicensePath = Join-Path $runtimeRoot 'python-runtime\LICENSE.txt'
    $licenseInput = $pythonLicense.Open()
    try {
        $licenseOutput = [System.IO.File]::Create($pythonLicensePath)
        try { $licenseInput.CopyTo($licenseOutput) }
        finally { $licenseOutput.Dispose() }
    }
    finally { $licenseInput.Dispose() }
}
finally { $pythonArchive.Dispose() }
$helpersRoot = Join-Path $releaseRoot "Helpers"
New-Item -ItemType Directory -Force -Path $helpersRoot | Out-Null
foreach ($launcher in @(
    "Connect-QproWireless.cmd", "Pair-QproWireless.cmd", "Launch-QproWireless.cmd",
    "Enable-QproWireless.cmd", "Disable-QproWireless.cmd",
    "Install-AMD-ROCm.cmd", "Launch-QproRocm.cmd"
)) {
    Copy-Item -LiteralPath (Join-Path $root $launcher) -Destination (Join-Path $helpersRoot $launcher)
}
Copy-Item -LiteralPath (Join-Path $root "RELEASE_HELPERS_README.md") -Destination (Join-Path $helpersRoot "README.md")
$docsRoot = Join-Path $releaseRoot "Docs"
New-Item -ItemType Directory -Force -Path $docsRoot | Out-Null
foreach ($document in @("LICENSE", "THIRD_PARTY_NOTICES.md", "UPSTREAM-README.md", "RELEASE_INSTRUCTIONS.md", "RELEASE_NOTES_V2.1.2.md", "CONTROLLER_INPUT.md", "GAZE_ENGINE_TEST_NOTES.md")) {
    $documentSource = Join-Path $root $document
    if (-not (Test-Path -LiteralPath $documentSource) -and $assetRootResolved) {
        $documentSource = Join-Path $assetRootResolved $document
    }
    if (-not (Test-Path -LiteralPath $documentSource)) { throw "Required document is missing: $document" }
    Copy-Item -LiteralPath $documentSource -Destination (Join-Path $docsRoot $document)
}
if (-not [string]::IsNullOrWhiteSpace($TestNotesPath)) {
    if (-not (Test-Path -LiteralPath $TestNotesPath -PathType Leaf)) {
        throw "Test build notes are missing: $TestNotesPath"
    }
    Copy-Item -LiteralPath $TestNotesPath -Destination (Join-Path $docsRoot "FIX_TEST_NOTES.md")
}
$guidePdf = Join-Path $root "Quest_Pro_Enhanced_Face_Tracking_Guide.pdf"
if (-not (Test-Path -LiteralPath $guidePdf -PathType Leaf)) {
    throw "Required beginner guide is missing: $guidePdf"
}
Copy-Item -LiteralPath $guidePdf -Destination (Join-Path $releaseRoot "Quest_Pro_Enhanced_Face_Tracking_Guide.pdf")

New-Item -ItemType Directory -Force -Path (Join-Path $runtimeRoot "captures"), (Join-Path $runtimeRoot "training"), (Join-Path $runtimeRoot "research\seacliff_eye_model") | Out-Null
Set-Content -LiteralPath (Join-Path $runtimeRoot "captures\.gitkeep") -Value ""
Set-Content -LiteralPath (Join-Path $runtimeRoot "training\.gitkeep") -Value ""
Set-Content -LiteralPath (Join-Path $runtimeRoot "research\seacliff_eye_model\.gitkeep") -Value ""

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

$userFolderMarker = ':' + [System.IO.Path]::DirectorySeparatorChar + 'Users' + [System.IO.Path]::DirectorySeparatorChar
$textExtensions = @('.json', '.md', '.ps1', '.py', '.txt', '.cmd', '.cs', '.csproj', '.c')
foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | Where-Object Extension -In $textExtensions) {
    $content = [System.IO.File]::ReadAllText($file.FullName).Replace('\\', '\')
    if ($content.IndexOf($userFolderMarker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "A personal Windows user path entered the release: $($file.FullName.Substring($releaseRoot.Length + 1))"
    }
}

# A .NET DLL can contain an absolute PDB path even when the PDB itself is not
# packaged. Scan every file in chunks so large executables stay cheap.
$userPathPattern = [regex]::new('(?i)[a-z]:[\\/]+users[\\/]+')
foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -Recurse -File) {
    $stream = [System.IO.File]::OpenRead($file.FullName)
    try {
        $buffer = [byte[]]::new(65536)
        $overlap = ''
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $chunk = $overlap + [System.Text.Encoding]::ASCII.GetString($buffer, 0, $count)
            # This also catches UTF-16 paths, whose ASCII bytes are separated by NULs.
            $chunk = $chunk.Replace([string][char]0, '')
            if ($userPathPattern.IsMatch($chunk)) {
                throw "A personal Windows user path entered a release binary: $($file.FullName.Substring($releaseRoot.Length + 1))"
            }
            $overlap = $chunk.Substring([Math]::Max(0, $chunk.Length - 64))
        }
    }
    finally { $stream.Dispose() }
}

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
# ZIP entry names use '/' on every platform. Compress-Archive writes Windows
# separators on some PowerShell versions, which breaks non-Windows extractors.
$archiveStream = [System.IO.File]::Open($archive, [System.IO.FileMode]::CreateNew)
try {
    $zipWriter = [System.IO.Compression.ZipArchive]::new(
        $archiveStream, [System.IO.Compression.ZipArchiveMode]::Create, $true
    )
    try {
        foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | Sort-Object FullName) {
            $relative = $file.FullName.Substring($distRoot.Length + 1).Replace('\', '/')
            $entry = $zipWriter.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $inputStream = [System.IO.File]::OpenRead($file.FullName)
            try {
                $outputStream = $entry.Open()
                try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            } finally {
                $inputStream.Dispose()
            }
        }
    } finally {
        $zipWriter.Dispose()
    }
} finally {
    $archiveStream.Dispose()
}

$releaseZip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    $archiveNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($archiveEntry in $releaseZip.Entries) {
        [void]$archiveNames.Add($archiveEntry.FullName.Replace('\', '/'))
    }
    $missingArchiveFiles = @(
        @($runtimeFiles) + @('python-runtime\python.3.12.10.nupkg', 'python-runtime\LICENSE.txt', 'python-runtime\README.txt') | Where-Object {
            $entry = "$releaseName/QproRuntime/$($_.Replace('\', '/'))"
            -not $archiveNames.Contains($entry)
        }
    )
    if ($missingArchiveFiles.Count) {
        throw "Required runtime files are missing from the ZIP: $($missingArchiveFiles -join ', ')"
    }
} finally {
    $releaseZip.Dispose()
}

$releaseZip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    $missingArchiveFiles = @(
        $runtimeFiles | Where-Object {
            $entry = "$releaseName/QproRuntime/$($_.Replace('\', '/'))"
            $null -eq $releaseZip.GetEntry($entry)
        }
    )
    if ($missingArchiveFiles.Count) {
        throw "Required runtime files are missing from the ZIP: $($missingArchiveFiles -join ', ')"
    }
} finally {
    $releaseZip.Dispose()
}

Write-Host "RELEASE_READY folder=$releaseRoot"
Write-Host "RELEASE_READY zip=$archive"
