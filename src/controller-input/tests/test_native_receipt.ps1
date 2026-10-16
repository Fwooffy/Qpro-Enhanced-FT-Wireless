$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $sourceRoot 'native-build-receipt.ps1')
$fixtureParent = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..\artifacts\native-receipt-tests'))
$fixture = Join-Path $fixtureParent ([Guid]::NewGuid().ToString('N'))
$fixtureSource = Join-Path $fixture 'source'
$fixtureStage = Join-Path $fixture 'stage'
$script:checks = 0
function Write-FixtureFile([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}
function Assert-Rejected([scriptblock]$Action, [string]$Context) {
    $rejected = $false
    try { & $Action } catch {
        $rejected = $_.Exception.Message.Contains('-ControllerInputAssetRoot')
    }
    if (-not $rejected) { throw "Freshness guard accepted $Context or omitted recovery instructions." }
    $script:checks++
}
try {
    foreach ($relative in Get-QproNativeInputPaths) {
        Write-FixtureFile (Join-Path $fixtureSource $relative) ('fixture input ' + $relative)
    }
    $sdk = '0123456789abcdef0123456789abcdef01234567'
    Write-FixtureFile (Join-Path $fixtureSource 'third_party/openvr/SDK_REVISION.txt') $sdk
    $resources = Get-QproNativeResourcePaths
    foreach ($relative in $resources.Keys) {
        Write-FixtureFile (Join-Path $fixtureSource $relative) ('fixture resource ' + $relative)
        Write-FixtureFile (Join-Path $fixtureStage $resources[$relative]) ('fixture resource ' + $relative)
    }
    $reader = Join-Path $fixtureStage 'qpro-controller-input'
    $driver = Join-Path $fixtureStage 'qpro_controller/bin/win64/driver_qpro_controller.dll'
    Write-FixtureFile $reader 'fixture reader bytes'
    Write-FixtureFile $driver 'fixture driver bytes'
    $receipt = [ordered]@{
        nativeBuildSchema = 1
        nativeInputHashes = Get-QproNativeHashes $fixtureSource (Get-QproNativeInputPaths)
        resourceHashes = Get-QproNativeHashes $fixtureSource @($resources.Keys)
        sdkRevision = $sdk
        readerSha256 = (Get-FileHash -LiteralPath $reader -Algorithm SHA256).Hash.ToLowerInvariant()
        driverSha256 = (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $marker = Join-Path $fixtureStage 'build-info.json'
    $metadata = $receipt | ConvertTo-Json -Depth 6
    Write-FixtureFile $marker $metadata
    Assert-QproNativeStage $fixtureSource $fixtureStage
    $script:checks++
    if ([IO.File]::ReadAllText($marker) -cne $metadata) { throw 'Read-only validation changed the receipt.' }
    $script:checks++

    Write-FixtureFile $marker '{"liveValidated":false,"driverSha256":"old-receipt"}'
    Assert-Rejected { Assert-QproNativeStage $fixtureSource $fixtureStage } 'an old stage receipt'
    Write-FixtureFile $marker $metadata
    foreach ($binary in @($reader, $driver)) {
        $original = [IO.File]::ReadAllText($binary)
        [IO.File]::AppendAllText($binary, ' tampered')
        Assert-Rejected { Assert-QproNativeStage $fixtureSource $fixtureStage } 'changed native binary bytes'
        Write-FixtureFile $binary $original
    }
    foreach ($relative in @('controller_logic.hpp', 'settings.hpp', 'headset_reader.c',
                            'third_party/openvr/openvr_driver.h', 'third_party/openvr/SDK_REVISION.txt')) {
        $path = Join-Path $fixtureSource $relative
        $original = [IO.File]::ReadAllText($path)
        [IO.File]::AppendAllText($path, ' changed input')
        Assert-Rejected { Assert-QproNativeStage $fixtureSource $fixtureStage } ('stale source ' + $relative)
        Write-FixtureFile $path $original
    }
    $changed = $metadata | ConvertFrom-Json
    $changed.sdkRevision = 'ffffffffffffffffffffffffffffffffffffffff'
    Write-FixtureFile $marker ($changed | ConvertTo-Json -Depth 6)
    Assert-Rejected { Assert-QproNativeStage $fixtureSource $fixtureStage } 'a changed SDK revision field'
    Write-FixtureFile $marker $metadata
    $stagedReadme = Join-Path $fixtureStage 'qpro_controller/README.md'
    [IO.File]::AppendAllText($stagedReadme, ' stale copy')
    Assert-Rejected { Assert-QproNativeStage $fixtureSource $fixtureStage } 'stale copied documentation'
    Write-FixtureFile $stagedReadme 'fixture resource README.md'
    $changed = $metadata | ConvertFrom-Json
    $changed.nativeInputHashes | Add-Member -NotePropertyName 'unrecognized-source.h' -NotePropertyValue ('0' * 64)
    Write-FixtureFile $marker ($changed | ConvertTo-Json -Depth 6)
    Assert-Rejected { Assert-QproNativeStage $fixtureSource $fixtureStage } 'an unrecognized input set'
    Write-FixtureFile $marker $metadata
    Remove-Item -LiteralPath $marker
    Assert-Rejected { Assert-QproNativeStage $fixtureSource $fixtureStage } 'a missing receipt'

    $builder = [IO.File]::ReadAllText((Join-Path $sourceRoot '..\build-release.ps1'))
    $checkAt = $builder.IndexOf('Assert-QproNativeStage ')
    $deleteAt = $builder.IndexOf('Remove-Item -LiteralPath $releaseRoot')
    if ($checkAt -lt 0 -or $deleteAt -lt 0 -or $checkAt -ge $deleteAt) {
        throw 'The release builder must check native freshness before replacing release output.'
    }
    $script:checks++
    Write-Host "PASS $script:checks native-stage freshness/input/SDK/binary/resource/preflight checks"
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($fixtureParent + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -notmatch '^[a-f0-9]{32}$') { throw 'Unsafe native receipt fixture cleanup.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
