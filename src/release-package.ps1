# Build-time ZIP layout shared by the release builder and its regression test.
function Get-QproReleaseArchiveRootName {
    param([Parameter(Mandatory = $true)][string]$PackageName)

    if ($PackageName -match '[\\/:*?"<>|\x00-\x1f]' -or
        $PackageName.EndsWith(' ') -or $PackageName.EndsWith('.') -or
        $PackageName -notmatch '^(QproFaceTracking|QproCompanion)(?=[. -]|$)') {
        throw "Invalid Qpro package folder name: $PackageName"
    }
    # V3.0.0 checks this folder name before reading the release manifest. Keep
    # its original name inside the ZIP even when the download uses Companion.
    return $PackageName -replace '^QproCompanion', 'QproFaceTracking'
}

function Write-QproReleaseArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseRoot,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    $sourceRoot = [System.IO.Path]::GetFullPath($ReleaseRoot).TrimEnd([char[]]'\/')
    $archiveRootName = Get-QproReleaseArchiveRootName ([System.IO.Path]::GetFileName($sourceRoot))
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Release folder does not exist: $sourceRoot"
    }
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
    # CreateNew refuses an existing archive; the caller owns any replacement.
    $archiveStream = [System.IO.File]::Open($ArchivePath, [System.IO.FileMode]::CreateNew)
    try {
        $zipWriter = [System.IO.Compression.ZipArchive]::new(
            $archiveStream, [System.IO.Compression.ZipArchiveMode]::Create, $true
        )
        try {
            foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Sort-Object FullName) {
                # ZIP paths always use '/', independently of the host platform.
                $relative = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
                $entry = $zipWriter.CreateEntry("$archiveRootName/$relative", [System.IO.Compression.CompressionLevel]::Optimal)
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
    return $archiveRootName
}
