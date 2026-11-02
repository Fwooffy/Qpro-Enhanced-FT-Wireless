[CmdletBinding()]
param(
    [string]$Dotnet = '',
    [string]$OutputRoot = '',
    [switch]$Run,
    [switch]$Open,
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
if ($Run -and $Open) { throw 'Choose -Run or -Open, not both.' }
$repository = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repository
if (-not $Dotnet) {
    $privateSdk = Join-Path $workspace '.dotnet-sdk\dotnet.exe'
    if (Test-Path -LiteralPath $privateSdk -PathType Leaf) { $Dotnet = $privateSdk }
    else { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
}
$Dotnet = (Resolve-Path -LiteralPath $Dotnet).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repository 'artifacts\local-updater' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$fixture = Join-Path $OutputRoot ('local-updater-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $fixture) { throw "Fixture already exists: $fixture" }
New-Item -ItemType Directory -Path $fixture | Out-Null
$nonce = [guid]::NewGuid().ToString('N')
$project = Join-Path $PSScriptRoot 'tests\hub-local-updater\HubLocalUpdaterTests.csproj'
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-FixtureText([string]$Path, [string]$Value) {
    $parent = Split-Path -Parent $Path
    [void][IO.Directory]::CreateDirectory($parent)
    [IO.File]::WriteAllText($Path, $Value, $utf8)
}
function Get-FixtureHash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Seal-Package([string]$Package) {
    $lines = @(Get-ChildItem -LiteralPath $Package -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Package.Length + 1).Replace('\', '/')
        (Get-FixtureHash $_.FullName) + '  ' + $relative
    })
    Write-FixtureText (Join-Path $Package 'SHA256SUMS.txt') (($lines -join "`n") + "`n")
}
function New-FixturePackage([string]$Package, [string]$Version, [string]$PublishedExe) {
    [void][IO.Directory]::CreateDirectory($Package)
    Copy-Item -LiteralPath $PublishedExe -Destination (Join-Path $Package 'QproFaceTracking.exe')
    $runtime = Join-Path $Package 'QproRuntime'
    foreach ($name in @('build-and-run.ps1', 'setup-runtime.ps1', 'runtime-python.ps1', 'receiver.py',
        'tongue_model_preview.py', 'runtime-recovery.ps1', 'tracking_budget.py',
        'platform-tools/adb.exe', 'vrcft-gaze-bridge/bin/Release/net10.0/Qpro.GazeBridge.dll')) {
        Write-FixtureText (Join-Path $runtime $name) "INERT LOCAL UPDATER FIXTURE $Version : $name. Never execute this placeholder."
    }
    foreach ($branch in @('gate', 'direction')) {
        Write-FixtureText (Join-Path $runtime "models/qpro-stereo-tongue-v15-$branch.pt") "Identical inert bundled $branch model fixture."
    }
    Write-FixtureText (Join-Path $runtime 'release-manifest.json') (@{
        name = 'QproFaceTracking'; version = $Version; updateFormat = 1
    } | ConvertTo-Json -Compress)
    Seal-Package $Package
}

$savedEnvironment = @{}
foreach ($name in @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_ROOT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_NOLOGO', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:DOTNET_ROOT = Split-Path -Parent $Dotnet
    $env:DOTNET_CLI_HOME = Join-Path $fixture 'dotnet-home'
    if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $workspace '.qpro-nuget-packages' }
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $emptyFeed = Join-Path $fixture 'empty-offline-feed'
    [void][IO.Directory]::CreateDirectory($emptyFeed)
    $publishRoots = @{}
    foreach ($version in @('2.9.99', '3.0.0')) {
        $publish = Join-Path $fixture "publish-$version"
        $intermediate = (Join-Path $fixture "obj-$version").Replace('\', '/') + '/'
        $properties = @("-p:Version=$version", "-p:BaseIntermediateOutputPath=$intermediate", '-p:PublishSingleFile=true', '-p:SelfContained=true', '-p:NuGetAudit=false')
        Write-Host "Building isolated updater fixture V$version from cached dependencies..."
        & $Dotnet restore $project -r win-x64 --source $emptyFeed @properties
        if ($LASTEXITCODE -ne 0) { throw 'Offline restore failed. This test requires the SDK/runtime packages already present in the NuGet cache; nothing was downloaded.' }
        & $Dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:DebugType=None -o $publish @properties
        if ($LASTEXITCODE -ne 0) { throw "Publishing local fixture V$version failed." }
        $publishedExe = Join-Path $publish 'QproFaceTracking.exe'
        if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) { throw "Missing fixture executable: $publishedExe" }
        $publishRoots[$version] = $publishedExe
    }
    $app = Join-Path $fixture 'app'
    $target = Join-Path $fixture 'payload\QproFaceTracking.V3.0.0'
    New-FixturePackage $app '2.9.99' $publishRoots['2.9.99']
    New-FixturePackage $target '3.0.0' $publishRoots['3.0.0']

    # Add private sample files only after sealing the source release. They are
    # deliberately absent from both release checksum lists and the new ZIP.
    $sentinels = @{
        'QproRuntime/models/qpro-stereo-tongue-v42-gate.pt' = 'Personal gate sentinel: preserve these exact bytes.'
        'QproRuntime/models/qpro-stereo-tongue-v42-direction.pt' = 'Personal direction sentinel: preserve these exact bytes.'
        'QproRuntime/models/qpro-stereo-tongue-v42.metadata.json' = '{"fixture":true,"version":42,"personal":true}'
        'QproRuntime/config/live-options.json' = '{"fixture":true,"personalOption":"keep"}'
        'QproRuntime/config/wireless-headset.json' = '{"fixture":true,"address":"192.0.2.1"}'
        'QproRuntime/captures/sample.qpcap' = 'Personal recording sentinel: preserve these exact bytes.'
    }
    $preserve = @{}
    foreach ($relative in $sentinels.Keys) {
        $path = Join-Path $app $relative
        Write-FixtureText $path $sentinels[$relative]
        $preserve[$relative] = Get-FixtureHash $path
    }
    Write-FixtureText (Join-Path $fixture 'preserve.json') (@{ files = $preserve } | ConvertTo-Json -Depth 4)
    $assets = Join-Path $fixture 'assets'
    [void][IO.Directory]::CreateDirectory($assets)
    $archive = Join-Path $assets 'QproCompanion.V3.0.0.zip'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $target -Recurse -File | Sort-Object FullName) {
                $relative = 'QproFaceTracking.V3.0.0/' + $file.FullName.Substring($target.Length + 1).Replace('\', '/')
                $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $inputStream = [IO.File]::OpenRead($file.FullName)
                try { $outputStream = $entry.Open(); try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() } }
                finally { $inputStream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    Write-FixtureText (Join-Path $fixture 'local-updater.json') (@{
        schema = 1; nonce = $nonce; sourceVersion = '2.9.99'; targetVersion = '3.0.0'
        archiveSha256 = Get-FixtureHash $archive; archiveSize = (Get-Item -LiteralPath $archive).Length
    } | ConvertTo-Json)
    Write-Host "LOCAL_UPDATER_FIXTURE=$fixture"
    Write-Host 'Prepared local dummy runtime and personal-data sentinels. No live installation was copied or modified.'
    if (-not ($Run -or $Open)) {
        Write-Host 'To open this prepared fixture, set QPRO_LOCAL_UPDATER_ROOT to the folder above and QPRO_LOCAL_UPDATER_NONCE to the nonce in local-updater.json, then launch app\QproFaceTracking.exe.'
        return
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $app 'QproFaceTracking.exe'
    $start.WorkingDirectory = $app
    $start.UseShellExecute = $false
    $start.EnvironmentVariables['QPRO_LOCAL_UPDATER_ROOT'] = $fixture
    $start.EnvironmentVariables['QPRO_LOCAL_UPDATER_NONCE'] = $nonce
    if ($Run) { $start.Arguments = '--automatic' }
    $parent = [Diagnostics.Process]::Start($start)
    if ($Open) { Write-Host "Opened local updater test (PID $($parent.Id)). Evidence stays in $fixture"; $parent.Dispose(); return }
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $receiptPath = Join-Path $fixture 'verification.json'
        $fixturePrefix = $fixture.TrimEnd('\') + '\'
        do {
            Start-Sleep -Milliseconds 250
            if (-not $parent.HasExited) { continue }
            if ($parent.ExitCode -ne 0) { throw "Initial fixture process exited with code $($parent.ExitCode). Evidence: $fixture" }
            if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { continue }
            try { $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json } catch { continue }
            if ($receipt.passed -ne $true -or $receipt.version -ne '3.0.0') { throw "Replacement verification failed. Read $receiptPath" }
            $fixtureProcesses = @(Get-Process | Where-Object {
                try {
                    $processPath = $_.Path
                    $processPath -and $processPath.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase) -and
                        [IO.Path]::GetFileName($processPath) -in @('QproFaceTracking.exe', 'QproUpdate.exe')
                } catch { $false }
            })
            if ($fixtureProcesses.Count -eq 0) {
                Write-Host "PASS: Local updater completed 2.9.99 -> 3.0.0, replacement verified preservation, and fixture processes exited. Receipt: $receiptPath"
                return
            }
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Timed out waiting for local updater verification and fixture process exit. No processes were killed; inspect any test dialog and evidence in $fixture"
    } finally { $parent.Dispose() }
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}
