param([string]$FixtureDll = "", [string]$OutputRoot = "")

$ErrorActionPreference = "Stop"
$sourceRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($FixtureDll)) {
    $FixtureDll = Join-Path $sourceRoot "vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll"
}
if ([System.Reflection.AssemblyName]::GetAssemblyName($FixtureDll).Name -ne "Qpro.GazeBridge") {
    throw "Pass an existing Qpro DLL. This test only reads its metadata and copies it; no module DLL is loaded or executed."
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $sourceRoot)) "outputs\test-module-preflight"
}
$outputFull = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
$fixtureRoot = Join-Path $outputFull ("fixture-" + [guid]::NewGuid().ToString("N"))
$runtime = Join-Path $fixtureRoot "runtime"
$appData = Join-Path $fixtureRoot "appdata"
$localData = Join-Path $fixtureRoot "localdata"
$customLibs = Join-Path $appData "VRCFaceTracking\CustomLibs"
$packagedModule = Join-Path $runtime "vrcft-gaze-bridge\bin\Release\net10.0\Qpro.GazeBridge.dll"
$oldAppData = $env:APPDATA
$oldLocalData = $env:LOCALAPPDATA
$assertions = 0
New-Item -ItemType Directory -Path $runtime, (Split-Path -Parent $packagedModule), $appData, $localData -Force | Out-Null
foreach ($name in @("install-vrcft-eye-bridge.ps1", "vrcft-module-installation.ps1")) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $runtime $name)
}
Copy-Item -LiteralPath $FixtureDll -Destination $packagedModule
[IO.File]::WriteAllText((Join-Path $runtime "release-manifest.json"), '{"name":"QproFaceTracking","version":"2.1.2"}')
. (Join-Path $runtime "vrcft-module-installation.ps1")

function Assert-Test([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}
function Expect-Failure([scriptblock]$Action, [string]$Description) {
    $message = $null
    try { $null = & $Action } catch { $message = $_.Exception.Message }
    Assert-Test (-not [string]::IsNullOrWhiteSpace($message)) "$Description was accepted by the tracking preflight."
}
function Expect-SourceConflict([scriptblock]$Action, [string]$OtherPath) {
    $message = $null
    try { $null = & $Action } catch { $message = $_.Exception.Message }
    Assert-Test (-not [string]::IsNullOrEmpty($message) -and $message.Contains($OtherPath) -and
        $message.Contains("Remove that other source module through VRCFaceTracking") -and
        $message.Contains("left those files unchanged")) "A competing source had no actionable preflight warning."
}
function Remove-FixtureTree([string]$Path) {
    # Never delete a computed test path until its absolute scope is verified.
    $full = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath($fixtureRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The test cleanup path escaped its own fixture: $full"
    }
    Assert-QproPathWithoutLinks $full
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
function Reset-Fixture([string]$TrackingSource) {
    Remove-FixtureTree $customLibs
    & (Join-Path $runtime "install-vrcft-eye-bridge.ps1") -TrackingSource $TrackingSource | Out-Null
}
function Assert-Preflight([string]$TrackingSource) {
    Assert-QproInstalledModule -TrackingSource $TrackingSource -CustomLibs $customLibs -PackagedModule $packagedModule
}
# The real installer runs only against this fixture. Its process guard is mocked
# to avoid coupling these disk tests to a live VRCFT session on the developer PC.
function Get-Process { param($Name, $ErrorAction); return @() }

try {
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localData
    Assert-Test ($null -ne (Get-Command Assert-QproInstalledModule -CommandType Function -ErrorAction SilentlyContinue)) "The shared tracking preflight is missing."
    $tokens = $null
    $errors = $null
    $launcher = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $sourceRoot "build-and-run.ps1"), [ref]$tokens, [ref]$errors)
    Assert-Test ($errors.Count -eq 0) "The production launcher did not parse under Windows PowerShell."
    $blocks = @($launcher.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -eq '$vrcftRequired'
    }, $true))
    Assert-Test ($blocks.Count -eq 1) "The production launcher must have one module preflight guard."
    $block = $blocks[0]
    $calls = @($block.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq "Assert-QproInstalledModule"
    }, $true))
    Assert-Test ($calls.Count -eq 1) "Camera startup is not wired to the shared module preflight."
    Assert-Test ($block.Extent.Text.Contains("vrcft-module-installation.ps1")) "The launcher did not import its shared module helper."
    # Execute this exact production guard, not a copied version of its logic.
    # Earlier launcher statements (ADB, process start and camera injection) are
    # deliberately absent from the probe.
    $launcherProbe = [scriptblock]::Create(('param([string]$FixtureRuntime, [string]$TrackingSource, [bool]$vrcftRequired = $true)' + "`n" +
        '$PSScriptRoot = $FixtureRuntime' + "`n" + $block.Extent.Text))

    foreach ($source in @("VirtualDesktop", "SteamLink")) {
        Reset-Fixture $source
        $identity = Get-QproModuleIdentity $source
        $folder = Join-Path $customLibs $identity.Id
        $dll = Join-Path $folder $identity.Dll
        $metadataPath = Join-Path $folder "module.json"
        $metadata = [IO.File]::ReadAllText($metadataPath)
        $before = Get-QproTreeHashes $customLibs $true
        Assert-Test (@(Get-ChildItem -LiteralPath $customLibs -File -Filter "*.dll").Count -eq 0) "$source fixture unexpectedly contains a loose DLL."
        $null = Assert-Preflight $source
        $null = & $launcherProbe $runtime $source
        Assert-QproSameTree $customLibs $true $before
        Assert-Test $true "$source GUID-folder installation was not accepted unchanged."

        # Native source DLLs are identified by names/cards, never loaded. Test
        # the shared preflight and the real launcher guard with both Qpro sources.
        foreach ($name in @("VirtualDesktop.dll", "LinkFT.dll", "VRCFT-Steam_Link.dll")) {
            $otherDll = Join-Path $customLibs $name
            [IO.File]::WriteAllText($otherDll, "unrelated native source fixture")
            $unchanged = Get-QproTreeHashes $customLibs $true
            Expect-SourceConflict { Assert-Preflight $source } $otherDll
            Expect-SourceConflict { & $launcherProbe $runtime $source } $otherDll
            Assert-QproSameTree $customLibs $true $unchanged
            Assert-Test $true "A competing loose source was modified by tracking preflight."
            Remove-Item -LiteralPath $otherDll
        }
        $otherFolder = Join-Path $customLibs "79ccecf5-1374-4808-9d22-4d69c5799fba"
        New-Item -ItemType Directory -Path $otherFolder | Out-Null
        $otherDll = Join-Path $otherFolder "NativeFixture.dll"
        $otherCard = Join-Path $otherFolder "module.json"
        [IO.File]::WriteAllText($otherDll, "unrelated native module fixture")
        foreach ($card in @(
            '{"ModuleName":"Virtual Desktop"}',
            '{"DllFileName":"VRCFT-SteamLink.dll"}',
            '{"ModuleId":"2a8c8080-2a76-46af-bf76-1da7c0127ef8"}',
            '{"ModulePageUrl":"https://github.com/danwillm/VRCFT-SteamLink"}'
        )) {
            [IO.File]::WriteAllText($otherCard, $card)
            $unchanged = Get-QproTreeHashes $customLibs $true
            Expect-SourceConflict { Assert-Preflight $source } $otherFolder
            Expect-SourceConflict { & $launcherProbe $runtime $source } $otherFolder
            Assert-QproSameTree $customLibs $true $unchanged
            Assert-Test $true "A competing card-identified source was modified by tracking preflight."
        }
        [IO.File]::WriteAllText($otherCard, '{"ModuleName":"Eye movement","ModuleDescription":"Works with Virtual Desktop"}')
        $null = Assert-Preflight $source
        Assert-Test $true "An unrelated feature module was mistaken for a competing face source."
        [IO.File]::WriteAllText($otherCard, '{broken')
        $null = Assert-Preflight $source
        Assert-Test $true "An unrelated unreadable card was mistaken for a competing face source."
        Remove-FixtureTree $otherFolder
        $officialFolder = Join-Path $customLibs "91a90618-b020-4064-8832-809b2ca2b3bc"
        New-Item -ItemType Directory -Path $officialFolder | Out-Null
        [IO.File]::WriteAllText((Join-Path $officialFolder "NativeFixture.dll"), "unrelated official source fixture")
        [IO.File]::WriteAllText((Join-Path $officialFolder "module.json"), '{broken')
        $unchanged = Get-QproTreeHashes $customLibs $true
        Expect-SourceConflict { Assert-Preflight $source } $officialFolder
        Expect-SourceConflict { & $launcherProbe $runtime $source } $officialFolder
        Assert-QproSameTree $customLibs $true $unchanged
        Assert-Test $true "A known official source was modified by tracking preflight."
        Remove-Item -LiteralPath (Join-Path $officialFolder "NativeFixture.dll")
        $null = Assert-Preflight $source
        Assert-Test $true "An empty official source directory prevented tracking."
        Remove-FixtureTree $officialFolder

        # Cloud Files tags are tested without altering this PC's OneDrive state.
        # The filesystem item seam exercises the production source guard while
        # real file reads, assembly identity and installed SHA-256 checks remain.
        foreach ($tag in 0..15) {
            $cloudTag = [uint32]2415919130 + [uint32]($tag * 4096)
            Assert-Test (Test-QproSourceReparseTag $cloudTag $false) "A Cloud Files file tag was rejected."
            Assert-Test (Test-QproSourceReparseTag $cloudTag $true) "A Cloud Files directory tag was rejected."
        }
        Assert-Test (-not (Test-QproSourceReparseTag ([uint32]2684354572) $false)) "A packaged file symlink was accepted."
        Assert-Test (-not (Test-QproSourceReparseTag ([uint32]2415919132) $true)) "An unknown projected filesystem tag was accepted."
        $originalSourceItem = (Get-Command Get-QproSourcePathItem).ScriptBlock
        $fixtureSourceTag = [uint32]2415919130
        function Get-QproSourcePathItem([string]$Path) {
            $item = & $originalSourceItem $Path
            if ($null -ne $item -and ($Path -eq $packagedModule -or $Path -eq $runtime)) {
                $item.Attributes = $item.Attributes -bor [IO.FileAttributes]::ReparsePoint
                $item.Tag = $fixtureSourceTag
            }
            return $item
        }
        try {
            $null = Assert-Preflight $source
            Assert-Test $true "A local cloud-flagged source was rejected."
            $fixtureSourceTag = [uint32]2415919132
            Expect-Failure { Assert-Preflight $source } "An unknown source reparse tag"
            $fixtureSourceTag = [uint32]2684354572
            Expect-Failure { Assert-Preflight $source } "A source file symlink"
        } finally { Set-Item -LiteralPath Function:\Get-QproSourcePathItem -Value $originalSourceItem }
        Expect-Failure { Assert-QproReadableSourcePath '\\fixture-host\fixture-share\Qpro.GazeBridge.dll' } "A network source path"

        $otherSource = if ($source -eq "SteamLink") { "VirtualDesktop" } else { "SteamLink" }
        Expect-Failure { Assert-Preflight $otherSource } "The opposite source"
        Expect-Failure { & $launcherProbe $runtime $otherSource } "The launcher's opposite source"

        $loose = Join-Path $customLibs $identity.Dll
        Copy-Item -LiteralPath $FixtureDll -Destination $loose
        Expect-Failure { Assert-Preflight $source } "Nested and loose duplicate modules"
        Remove-Item -LiteralPath $loose
        foreach ($name in @("000-Qpro.IndependentGaze.dll", "CopiedQpro.dll")) {
            $copied = Join-Path $customLibs $name
            Copy-Item -LiteralPath $FixtureDll -Destination $copied
            Expect-Failure { Assert-Preflight $source } "A copied/legacy root module"
            Remove-Item -LiteralPath $copied
        }
        $foreign = Join-Path $customLibs "91a90618-b020-4064-8832-809b2ca2b3bc"
        New-Item -ItemType Directory -Path $foreign | Out-Null
        $foreignDll = Join-Path $foreign "OfficialFixture.dll"
        Copy-Item -LiteralPath $FixtureDll -Destination $foreignDll
        Expect-Failure { Assert-Preflight $source } "A Qpro DLL copied into a foreign module folder"
        Remove-FixtureTree $foreign

        $otherIdentity = Get-QproModuleIdentity $otherSource
        $otherFolder = Join-Path $customLibs $otherIdentity.Id
        New-Item -ItemType Directory -Path $otherFolder | Out-Null
        Copy-Item -LiteralPath $FixtureDll -Destination (Join-Path $otherFolder $otherIdentity.Dll)
        $otherCard = $metadata | ConvertFrom-Json
        $otherCard.ModuleId = $otherIdentity.Id
        $otherCard.DllFileName = $otherIdentity.Dll
        $otherCard.ModuleName = "QproFaceTracking - " + $otherIdentity.Name
        [IO.File]::WriteAllText((Join-Path $otherFolder "module.json"), ($otherCard | ConvertTo-Json -Depth 5))
        Expect-Failure { Assert-Preflight $source } "Two source GUID modules"
        Remove-FixtureTree $otherFolder

        foreach ($change in @(
            @{ Key = "ModuleId"; Value = $otherIdentity.Id },
            @{ Key = "DllFileName"; Value = "..\" + $identity.Dll },
            @{ Key = "IsLocal"; Value = "true" },
            @{ Key = "AuthorName"; Value = "Someone else" },
            @{ Key = "ModuleName"; Value = "Unrelated source card" },
            @{ Key = "Version"; Value = "fixture" },
            @{ Key = "FileHash"; Value = ('0' * 32) }
        )) {
            $card = $metadata | ConvertFrom-Json
            $card.($change.Key) = $change.Value
            [IO.File]::WriteAllText($metadataPath, ($card | ConvertTo-Json -Depth 5))
            Expect-Failure { Assert-Preflight $source } ("An invalid module card " + $change.Key)
        }
        # Windows PowerShell collapses duplicate JSON properties. The original
        # card must still be rejected, including escaped spellings of a key.
        foreach ($duplicateName in @('IsLocal', 'Is\u004cocal')) {
            $duplicateJson = $metadata.Trim().TrimEnd('}') + ',"' + $duplicateName + '":true}'
            [IO.File]::WriteAllText($metadataPath, $duplicateJson)
            Expect-Failure { Assert-Preflight $source } "Duplicate root module-card fields"
        }
        foreach ($field in @("ModuleId", "DllFileName", "AuthorName", "ModuleName", "Version", "FileHash")) {
            $card = $metadata | ConvertFrom-Json
            $card.$field = @($card.$field)
            [IO.File]::WriteAllText($metadataPath, ($card | ConvertTo-Json -Depth 5))
            Expect-Failure { Assert-Preflight $source } ("A one-element array in string field " + $field)
        }
        $card = $metadata | ConvertFrom-Json
        $card.UsageInstructions = 'Example {"IsLocal":false,"nested":[{"value":"quoted: \"text\""}]} and path C:\example; colon: allowed.'
        [IO.File]::WriteAllText($metadataPath, ($card | ConvertTo-Json -Depth 5))
        $null = Assert-Preflight $source
        $null = & $launcherProbe $runtime $source
        Assert-Test $true "Braces, escaped quotes, colons and embedded JSON in a string invalidated a valid module card."

        $card = $metadata | ConvertFrom-Json
        $nested = [pscustomobject]@{ Leaf = "fixture" }
        1..8 | ForEach-Object { $nested = [pscustomobject]@{ Child = $nested } }
        $card | Add-Member -NotePropertyName Extra -NotePropertyValue $nested
        [IO.File]::WriteAllText($metadataPath, ($card | ConvertTo-Json -Depth 15))
        Expect-Failure { Assert-Preflight $source } "Excessive module-card nesting"
        $card = $metadata | ConvertFrom-Json
        1..32 | ForEach-Object { $card | Add-Member -NotePropertyName ("Extra" + $_) -NotePropertyValue $_ }
        [IO.File]::WriteAllText($metadataPath, ($card | ConvertTo-Json -Depth 5))
        Expect-Failure { Assert-Preflight $source } "Excessive module-card fields"
        foreach ($badJson in @('{broken', '[{"ModuleId":"' + $identity.Id + '"}]', ('x' * 65537))) {
            [IO.File]::WriteAllText($metadataPath, $badJson)
            Expect-Failure { Assert-Preflight $source } "Malformed or oversized module metadata"
        }
        Remove-Item -LiteralPath $metadataPath
        Expect-Failure { Assert-Preflight $source } "Missing module metadata"
        [IO.File]::WriteAllText($metadataPath, $metadata)
        [IO.File]::WriteAllText((Join-Path $folder "Unexpected.dll"), "Not a module")
        Expect-Failure { Assert-Preflight $source } "An unexpected DLL in the Qpro folder"
        Remove-Item -LiteralPath (Join-Path $folder "Unexpected.dll")

        # The installed DLL still reads as Qpro, and its card is refreshed to the
        # new MD5. Only comparison with the packaged SHA-256 reveals the update.
        $append = [IO.File]::Open($dll, [IO.FileMode]::Append)
        try { $append.WriteByte(0) } finally { $append.Dispose() }
        $card = $metadata | ConvertFrom-Json
        $card.FileHash = (Get-FileHash -LiteralPath $dll -Algorithm MD5).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($metadataPath, ($card | ConvertTo-Json -Depth 5))
        Expect-Failure { Assert-Preflight $source } "A different DLL with a self-consistent module card"
        Expect-Failure { & $launcherProbe $runtime $source } "The launcher's different binary"
        Copy-Item -LiteralPath $FixtureDll -Destination $dll -Force
        [IO.File]::WriteAllText($metadataPath, $metadata)

        $packageBackup = Join-Path (Split-Path -Parent $packagedModule) "fixture-package.saved"
        Move-Item -LiteralPath $packagedModule -Destination $packageBackup
        try {
            Expect-Failure { Assert-Preflight $source } "A missing packaged module"
            Expect-Failure { & $launcherProbe $runtime $source } "The launcher's missing packaged module"
        } finally { Move-Item -LiteralPath $packageBackup -Destination $packagedModule }
        $null = Assert-Preflight $source

        Remove-FixtureTree $folder
        Copy-Item -LiteralPath $FixtureDll -Destination $loose
        Expect-Failure { Assert-Preflight $source } "A loose legacy module without its current GUID card"
        Expect-Failure { & $launcherProbe $runtime $source } "The launcher's loose legacy module"
        Remove-Item -LiteralPath $loose
    }

    # No tracking feature means this launcher guard must not require a module.
    Remove-FixtureTree $customLibs
    $null = & $launcherProbe $runtime "VirtualDesktop" $false
    Assert-Test $true "The module guard ran with vrcftRequired disabled."
    Write-Host "PASS: $assertions module preflight checks under PowerShell $($PSVersionTable.PSVersion). No ADB, headset, real APPDATA or installed module was used. Fixture: $fixtureRoot"
} finally {
    $env:APPDATA = $oldAppData
    $env:LOCALAPPDATA = $oldLocalData
    # Retain only the parent output directory; remove this run's isolated files.
    $full = [IO.Path]::GetFullPath($fixtureRoot)
    if ([IO.Path]::GetDirectoryName($full).Equals($outputFull, [StringComparison]::OrdinalIgnoreCase) -and
        ([IO.Path]::GetFileName($full) -match '^fixture-[0-9a-f]{32}$')) {
        Assert-QproPathWithoutLinks $full
        Remove-Item -LiteralPath $full -Recurse -Force
    } else { throw "The test refused cleanup outside its verified fixture directory." }
}
