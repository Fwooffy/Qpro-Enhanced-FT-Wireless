$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-package.ps1')

$checks = 0
function Assert-Package([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}

$artifacts = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))
$testRoot = Join-Path $artifacts ('release-package-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    foreach ($packageName in @('QproFaceTracking.V3.0.2', 'QproCompanion.V3.0.2', 'QproCompanion V3.0.2 Test')) {
        $releaseRoot = Join-Path $testRoot $packageName
        New-Item -ItemType Directory -Path (Join-Path $releaseRoot 'QproRuntime\models') -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'QproFaceTracking.exe'), 'test executable, never run')
        [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'QproRuntime\release-manifest.json'), '{"name":"QproFaceTracking","version":"3.0.2","updateFormat":1}')
        [System.IO.File]::WriteAllBytes((Join-Path $releaseRoot 'QproRuntime\models\fixture.pt'), [byte[]]@(0, 10, 127, 128, 255))
        $expected = @{}
        foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -Recurse -File) {
            $relative = $file.FullName.Substring($releaseRoot.Length + 1).Replace('\', '/')
            $expected[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $sums = ($expected.Keys | Sort-Object | ForEach-Object { "$($expected[$_])  $_" }) -join "`n"
        [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), $sums)
        $expected['SHA256SUMS.txt'] = (Get-FileHash -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant()

        $archivePath = Join-Path $testRoot ($packageName + '.zip')
        $actualRoot = Write-QproReleaseArchive -ReleaseRoot $releaseRoot -ArchivePath $archivePath
        $expectedRoot = $packageName -replace '^QproCompanion', 'QproFaceTracking'
        Assert-Package ($actualRoot -ceq $expectedRoot) "Wrong archive root for $packageName"
        Assert-Package (Test-Path -LiteralPath $releaseRoot -PathType Container) 'Packaging renamed the staging folder.'
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            Assert-Package ($zip.Entries.Count -eq $expected.Count) 'Packaging changed the supplied file count.'
            foreach ($entry in $zip.Entries) {
                Assert-Package ($entry.FullName.StartsWith($expectedRoot + '/', [StringComparison]::Ordinal)) 'An entry does not use the legacy app folder.'
                Assert-Package (-not $entry.FullName.Contains('\')) 'ZIP paths must use forward slashes.'
                $relative = $entry.FullName.Substring($expectedRoot.Length + 1)
                Assert-Package ($expected.ContainsKey($relative)) "An unexpected file entered the ZIP: $relative"
                $stream = $entry.Open()
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
                finally { $sha.Dispose(); $stream.Dispose() }
                Assert-Package ($digest -ceq $expected[$relative]) "Packaging changed file bytes: $relative"
            }
            foreach ($required in @('QproFaceTracking.exe', 'QproRuntime/release-manifest.json', 'SHA256SUMS.txt')) {
                Assert-Package ($null -ne $zip.GetEntry("$expectedRoot/$required")) "Legacy updater cannot locate $required"
            }
        } finally { $zip.Dispose() }
        $originalHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        $refusedOverwrite = $false
        try { Write-QproReleaseArchive -ReleaseRoot $releaseRoot -ArchivePath $archivePath | Out-Null }
        catch { $refusedOverwrite = $true }
        Assert-Package $refusedOverwrite 'Archive writer silently replaced an existing ZIP.'
        Assert-Package ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -eq $originalHash) 'A refused overwrite changed the archive.'
    }

    foreach ($invalid in @('unrelated', '../QproCompanion', 'QproCompanion/elsewhere', 'QproCompanion.V3.0.2.', 'QproCompanion.V3.0.2 ')) {
        $refused = $false
        try { Get-QproReleaseArchiveRootName $invalid | Out-Null } catch { $refused = $true }
        Assert-Package $refused "Unsafe or incompatible package name was accepted: $invalid"
    }
    Write-Host "Release packaging passed $checks checks; branded downloads retain the legacy updater layout."
} finally {
    $resolved = [System.IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($artifacts + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($resolved).StartsWith('release-package-test-', [StringComparison]::Ordinal)) {
        throw 'Refusing to remove an unexpected test folder.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
