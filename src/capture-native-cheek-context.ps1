[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [ValidateRange(1, 10)][int]$SecondsPerPose = 3,
    [string]$InputMapName = 'VirtualDesktop.BodyState'
)

$ErrorActionPreference = 'Stop'

# Run this only when the wearer is ready in Virtual Desktop. It opens the
# existing PC shared-memory feed with read access; it does not start tracking,
# attach to the headset, change module settings, or send expression output.
$destination = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $destination) { throw "Capture already exists: $destination" }
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($destination)) -PathType Container)) {
    throw 'The output directory must already exist.'
}

$names = @(
    'BrowLowererL', 'BrowLowererR', 'CheekPuffL', 'CheekPuffR',
    'CheekRaiserL', 'CheekRaiserR', 'CheekSuckL', 'CheekSuckR',
    'ChinRaiserB', 'ChinRaiserT', 'DimplerL', 'DimplerR',
    'EyesClosedL', 'EyesClosedR', 'EyesLookDownL', 'EyesLookDownR',
    'EyesLookLeftL', 'EyesLookLeftR', 'EyesLookRightL', 'EyesLookRightR',
    'EyesLookUpL', 'EyesLookUpR', 'InnerBrowRaiserL', 'InnerBrowRaiserR',
    'JawDrop', 'JawSidewaysLeft', 'JawSidewaysRight', 'JawThrust',
    'LidTightenerL', 'LidTightenerR', 'LipCornerDepressorL', 'LipCornerDepressorR',
    'LipCornerPullerL', 'LipCornerPullerR', 'LipFunnelerLb', 'LipFunnelerLt',
    'LipFunnelerRb', 'LipFunnelerRt', 'LipPressorL', 'LipPressorR',
    'LipPuckerL', 'LipPuckerR', 'LipStretcherL', 'LipStretcherR',
    'LipSuckLb', 'LipSuckLt', 'LipSuckRb', 'LipSuckRt', 'LipTightenerL', 'LipTightenerR',
    'LipsToward', 'LowerLipDepressorL', 'LowerLipDepressorR', 'MouthLeft', 'MouthRight',
    'NoseWrinklerL', 'NoseWrinklerR', 'OuterBrowRaiserL', 'OuterBrowRaiserR',
    'UpperLidRaiserL', 'UpperLidRaiserR', 'UpperLipRaiserL', 'UpperLipRaiserR',
    'TongueTipInterdental', 'TongueTipAlveolar', 'TongueFrontDorsalPalate',
    'TongueMidDorsalPalate', 'TongueBackDorsalVelar', 'TongueOut', 'TongueRetreat'
)
$poses = @(
    @{ Name = 'relaxed'; Prompt = 'Relax your lips and both cheeks.' },
    @{ Name = 'pursed-no-puff'; Prompt = 'Purse your lips without puffing either cheek.' },
    @{ Name = 'left-puff'; Prompt = 'Puff only your left cheek.' },
    @{ Name = 'right-puff'; Prompt = 'Puff only your right cheek.' },
    @{ Name = 'both-puff'; Prompt = 'Puff both cheeks with your normal lip position.' },
    @{ Name = 'both-puff-and-purse'; Prompt = 'Keep both cheeks puffed while pursing your lips.' }
)
$map = $null
$view = $null
$writer = $null
try {
    $map = [IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting(
        $InputMapName, [IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
    $view = $map.CreateViewAccessor(0, 360, [IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
    $stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    $writer.WriteLine((@{
        type = 'metadata'; version = 1; source = 'virtual-desktop'; inputMapName = $InputMapName;
        recordedUtc = [DateTime]::UtcNow.ToString('o'); expressionNames = $names;
        poses = @($poses | ForEach-Object { $_.Name }); secondsPerPose = $SecondsPerPose
    } | ConvertTo-Json -Depth 4 -Compress))
    $first = [byte[]]::new(360)
    $second = [byte[]]::new(360)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    foreach ($pose in $poses) {
        Write-Host $pose.Prompt
        [void](Read-Host 'Hold the pose, then press Enter to record')
        $started = $clock.ElapsedMilliseconds
        $accepted = 0
        $dropped = 0
        do {
            [void]$view.ReadArray(0, $first, 0, $first.Length)
            [void]$view.ReadArray(0, $second, 0, $second.Length)
            $consistent = $true
            for ($index = 0; $index -lt $first.Length; $index++) {
                if ($first[$index] -ne $second[$index]) { $consistent = $false; break }
            }
            if ($consistent -and ($second[0] -band 1) -ne 0) {
                $weights = [single[]]::new(70)
                for ($index = 0; $index -lt $weights.Length; $index++) {
                    $weights[$index] = [BitConverter]::ToSingle($second, 4 + $index * 4)
                }
                $writer.WriteLine((@{
                    type = 'sample'; pose = $pose.Name; elapsedMs = $clock.ElapsedMilliseconds;
                    poseMs = $clock.ElapsedMilliseconds - $started;
                    faceFlags = $second[0]; weights = $weights
                } | ConvertTo-Json -Depth 4 -Compress))
                $accepted++
            } else { $dropped++ }
            Start-Sleep -Milliseconds 20
        } while ($clock.ElapsedMilliseconds - $started -lt $SecondsPerPose * 1000)
        $writer.Flush()
        Write-Host "$($pose.Name): $accepted valid samples, $dropped invalid or changing snapshots."
        if ($accepted -eq 0) { throw 'No valid lower-face samples. Check the Virtual Desktop face tracking feed.' }
    }
    $writer.WriteLine((@{ type = 'complete'; elapsedMs = $clock.ElapsedMilliseconds } | ConvertTo-Json -Compress))
    Write-Host "Saved raw cheek and mouth context to $destination"
} finally {
    if ($null -ne $writer) { $writer.Dispose() }
    if ($null -ne $view) { $view.Dispose() }
    if ($null -ne $map) { $map.Dispose() }
}
