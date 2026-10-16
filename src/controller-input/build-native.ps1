[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Zig,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\controller-native-build')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-build-receipt.ps1')
$nativeInputHashes = Get-QproNativeHashes $PSScriptRoot (Get-QproNativeInputPaths)
$compiler = (Resolve-Path -LiteralPath $Zig).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $output '.zig-cache'
$reader = Join-Path $output 'qpro-controller-input'
$logicTest = Join-Path $output 'test-controller-logic.exe'
$readerTest = Join-Path $output 'test-controller-reader.exe'
$dashboardTest = Join-Path $output 'test-dashboard-scroll.exe'
$adoptionTest = Join-Path $output 'test-controller-adoption.exe'
$mouseTest = Join-Path $output 'test-mouse-smoothing.exe'
$swipeTest = Join-Path $output 'test-swipe-render.exe'
$addon = Join-Path $output 'qpro_controller'
$binaryDirectory = Join-Path $addon 'bin\win64'
[IO.Directory]::CreateDirectory($binaryDirectory) | Out-Null
function Invoke-Compiler([string[]]$CompilerArguments) {
    $portableFlags = @('-s', "-ffile-prefix-map=$PSScriptRoot=controller-input", "-ffile-prefix-map=$(Split-Path $compiler -Parent)=zig")
    & $compiler @CompilerArguments @portableFlags
    if ($LASTEXITCODE -ne 0) { throw "Native compiler failed with code $LASTEXITCODE." }
}
Push-Location -LiteralPath $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'tests\test_native_receipt.ps1')
    # Exercise the real adoption function against fake OpenVR state, including
    # failed component creation and verified profile restoration.
    $providerSource = Get-Content -LiteralPath 'steamvr_provider.cpp' -Raw
    $adoptStart = $providerSource.IndexOf('void adopt(TrackedDeviceIndex_t id) {', [StringComparison]::Ordinal)
    if ($adoptStart -lt 0) { throw 'Controller adoption test target was not found.' }
    $adoptEnd = $providerSource.IndexOf("`n// Wrap the public server-driver ABI", $adoptStart, [StringComparison]::Ordinal)
    if ($adoptEnd -lt 0) { throw 'Controller adoption test boundary was not found.' }
    [IO.File]::WriteAllText((Join-Path $output 'adopt_under_test.inc'), $providerSource.Substring($adoptStart, $adoptEnd - $adoptStart), [Text.UTF8Encoding]::new($false))
    Invoke-Compiler @('c++', '-target', 'x86_64-windows-gnu', '-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror', 'tests/test_logic.cpp', '-o', $logicTest)
    Invoke-Compiler @('cc', '-target', 'x86_64-windows-gnu', '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror', 'tests/test_reader.c', '-o', $readerTest)
    Invoke-Compiler @('c++', '-target', 'x86_64-windows-gnu', '-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror', 'tests/test_dashboard_scroll.cpp', '-o', $dashboardTest)
    Invoke-Compiler @('c++', '-target', 'x86_64-windows-gnu', '-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror', "-I$output", 'tests/test_adoption.cpp', '-o', $adoptionTest)
    Invoke-Compiler @('c++', '-target', 'x86_64-windows-gnu', '-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror', 'tests/test_mouse_smoothing.cpp', '-o', $mouseTest)
    Invoke-Compiler @('c++', '-target', 'x86_64-windows-gnu', '-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror', 'tests/test_swipe_render.cpp', '-o', $swipeTest)
    & $logicTest
    if ($LASTEXITCODE -ne 0) { throw "Controller logic checks failed with code $LASTEXITCODE." }
    & $readerTest
    if ($LASTEXITCODE -ne 0) { throw "Fake-page/lifetime checks failed with code $LASTEXITCODE." }
    & $dashboardTest
    if ($LASTEXITCODE -ne 0) { throw "Dashboard scroll checks failed with code $LASTEXITCODE." }
    & $adoptionTest
    if ($LASTEXITCODE -ne 0) { throw "Controller adoption/rollback checks failed with code $LASTEXITCODE." }
    & $mouseTest
    if ($LASTEXITCODE -ne 0) { throw "Mouse smoothing/parity checks failed with code $LASTEXITCODE." }
    & $swipeTest
    if ($LASTEXITCODE -ne 0) { throw "Swipe render smoothing/reset checks failed with code $LASTEXITCODE." }
    Invoke-Compiler @('cc', '-target', 'aarch64-linux-musl', '-static', '-std=c11', '-O2', '-Wall', '-Wextra', '-Werror', 'headset_reader.c', '-o', $reader, '-lm')
    $driver = Join-Path $binaryDirectory 'driver_qpro_controller.dll'
    Invoke-Compiler @('c++', '-target', 'x86_64-windows-gnu', '-std=c++17', '-O2', '-shared', '-Wall', '-Wextra', '-Werror', 'steamvr_provider.cpp', '-o', $driver, '-lws2_32', '-luser32')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'addon\driver.vrdrivermanifest') -Destination $addon -Force
    [IO.Directory]::CreateDirectory((Join-Path $addon 'resources\input')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'addon\resources\settings.json') -Destination (Join-Path $addon 'resources\settings.json') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'addon\resources\input\quest_pro_touchpad.json') -Destination (Join-Path $addon 'resources\input\quest_pro_touchpad.json') -Force
    [IO.Directory]::CreateDirectory((Join-Path $addon 'resources\localization')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'addon\resources\localization\localization.json') -Destination (Join-Path $addon 'resources\localization\localization.json') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third_party\openvr\LICENSE') -Destination (Join-Path $addon 'LICENSE.OpenVR.txt') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $addon 'README.md') -Force
    $record = [ordered]@{
        nativeBuildSchema = 1
        nativeInputHashes = $nativeInputHashes
        resourceHashes = Get-QproNativeHashes $PSScriptRoot @((Get-QproNativeResourcePaths).Keys)
        liveValidated = $false
        firmwareProfile = 'legacy-51503870024400340'
        sdkRevision = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'third_party\openvr\SDK_REVISION.txt') -Raw).Trim()
        readerSha256 = (Get-FileHash -LiteralPath $reader -Algorithm SHA256).Hash.ToLowerInvariant()
        driverSha256 = (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding UTF8
    Assert-QproNativeStage $PSScriptRoot $output
    Write-Output "Native prototype built; staged add-on: $addon"
    Write-Output 'Only offline test programs were executed. Headset and SteamVR validation remain pending.'
}
finally { Pop-Location }
