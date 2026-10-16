# Build-only freshness receipt. Hashes detect stale/mixed local stages; they do
# not attest where a binary came from or establish live hardware compatibility.
function Get-QproNativeInputPaths {
    @('headset_reader.c', 'sensor_profile.h', 'reader_lifetime.h', 'protocol.h',
        'steamvr_provider.cpp', 'controller_logic.hpp', 'settings.hpp',
        'driver_health.hpp', 'dashboard_scroll.hpp',
        'third_party/openvr/openvr_driver.h', 'third_party/openvr/SDK_REVISION.txt',
        'build-native.ps1', 'native-build-receipt.ps1')
}

function Get-QproNativeResourcePaths {
    [ordered]@{
        'addon/driver.vrdrivermanifest' = 'qpro_controller/driver.vrdrivermanifest'
        'addon/resources/settings.json' = 'qpro_controller/resources/settings.json'
        'addon/resources/input/quest_pro_touchpad.json' = 'qpro_controller/resources/input/quest_pro_touchpad.json'
        'addon/resources/localization/localization.json' = 'qpro_controller/resources/localization/localization.json'
        'third_party/openvr/LICENSE' = 'qpro_controller/LICENSE.OpenVR.txt'
        'README.md' = 'qpro_controller/README.md'
    }
}

function Get-QproNativeHashes([string]$SourceRoot, [string[]]$Paths) {
    $hashes = [ordered]@{}
    foreach ($relative in $Paths) {
        $path = Join-Path $SourceRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Native build input is missing: $relative"
        }
        $hashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $hashes
}

function Assert-QproNativeStage([string]$SourceRoot, [string]$AssetRoot) {
    try {
        $marker = Join-Path $AssetRoot 'build-info.json'
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
            (Get-Item -LiteralPath $marker).Length -gt 65536) {
            throw 'The native build-info.json receipt is missing or unsupported.'
        }
        $receipt = [IO.File]::ReadAllText($marker) | ConvertFrom-Json
        if ($receipt -isnot [pscustomobject] -or $receipt.nativeBuildSchema -ne 1) {
            throw 'The stage has no current native-input freshness receipt.'
        }
        $expectedInputs = Get-QproNativeHashes $SourceRoot (Get-QproNativeInputPaths)
        $resources = Get-QproNativeResourcePaths
        $expectedResources = Get-QproNativeHashes $SourceRoot @($resources.Keys)
        foreach ($group in @(@{ Name = 'nativeInputHashes'; Expected = $expectedInputs },
                              @{ Name = 'resourceHashes'; Expected = $expectedResources })) {
            $recorded = $receipt.($group.Name)
            if ($recorded -isnot [pscustomobject] -or
                @($recorded.PSObject.Properties).Count -ne $group.Expected.Count) {
                throw "The $($group.Name) receipt has an unsupported input set."
            }
            foreach ($relative in $group.Expected.Keys) {
                $property = $recorded.PSObject.Properties[$relative]
                if ($null -eq $property -or $property.Value -isnot [string] -or
                    $property.Value -cne $group.Expected[$relative]) {
                    throw "The native stage is stale for $relative."
                }
            }
        }
        $revision = [IO.File]::ReadAllText((Join-Path $SourceRoot 'third_party/openvr/SDK_REVISION.txt')).Trim()
        if ($receipt.sdkRevision -cne $revision) { throw 'The staged OpenVR SDK revision differs from the source.' }
        foreach ($binary in @(@{ Path = 'qpro-controller-input'; Hash = $receipt.readerSha256 },
                              @{ Path = 'qpro_controller/bin/win64/driver_qpro_controller.dll'; Hash = $receipt.driverSha256 })) {
            $path = Join-Path $AssetRoot $binary.Path
            if ($binary.Hash -isnot [string] -or $binary.Hash -cnotmatch '^[a-f0-9]{64}$' -or
                -not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binary.Hash) {
                throw "The staged native binary differs from its receipt: $($binary.Path)."
            }
        }
        foreach ($relative in $resources.Keys) {
            $path = Join-Path $AssetRoot $resources[$relative]
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedResources[$relative]) {
                throw "The staged native resource differs from the source: $($resources[$relative])."
            }
        }
    }
    catch {
        throw ("Controller native stage is missing, stale or changed: $($_.Exception.Message) " +
            'Run controller-input\build-native.ps1 for the current source and supply its output through ' +
            '-ControllerInputAssetRoot. No release output has been replaced by this check.')
    }
}
