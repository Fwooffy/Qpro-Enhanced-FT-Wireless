param([string]$FixtureRoot = (Join-Path $PSScriptRoot 'artifacts\release-metadata-fixtures'))

$ErrorActionPreference = 'Stop'
function Import-PureFunction([string]$Path, [string]$Name) {
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true)
    if ($null -eq $definition) { throw "Missing pure helper: $Name" }
    . ([scriptblock]::Create('function global:' + $definition.Extent.Text.Substring('function '.Length)))
}
foreach ($name in @('Initialize-QproSourcePathNative', 'Test-QproSourceReparseTag',
    'Get-QproSourcePathItem', 'Assert-QproSourceAncestors', 'Assert-QproReadableSourcePath', 'Get-QproModuleVersion')) {
    Import-PureFunction (Join-Path $PSScriptRoot 'vrcft-module-installation.ps1') $name
}
Import-PureFunction (Join-Path $PSScriptRoot 'build-github-source.ps1') 'Get-QproPublicTestSources'

$fixtureParent = [System.IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\', '/')
$fixture = Join-Path $fixtureParent ('metadata-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$checks = 0
function Assert-Equal($Expected, $Actual, [string]$Context) {
    if ($Expected -ne $Actual) { throw "$Context expected '$Expected', found '$Actual'" }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Context) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted $Context" }
    $script:checks++
}
try {
    $runtime = Join-Path $fixture 'QproRuntime'
    New-Item -ItemType Directory -Path $runtime | Out-Null
    Assert-Equal '3.0.0' (Get-QproModuleVersion $runtime) 'Explicit development fallback'
    Assert-Equal '9.3.1' (Get-QproModuleVersion $runtime '9.3.1') 'Selected development fallback'
    Assert-Rejected { Get-QproModuleVersion $runtime 'bad' } 'Malformed development version'
    $manifestPath = Join-Path $runtime 'release-manifest.json'
    [System.IO.File]::WriteAllText($manifestPath, '{"name":"QproFaceTracking","version":"2.4.7"}')
    Assert-Equal '2.4.7' (Get-QproModuleVersion $runtime) 'Packaged module card version'
    foreach ($payload in @('{invalid', '{}', '{"name":"Other","version":"2.4.7"}',
        '{"name":"QproFaceTracking","version":214}', '{"name":"QproFaceTracking","version":"v2.4.7"}',
        '{"name":"QproFaceTracking","version":"2.4.7-rc1"}', '{"name":"QproFaceTracking","version":"02.4.7"}',
        '{"name":"QproFaceTracking","version":"2.4.7.0"}', '{"name":"QproFaceTracking","version":"99999999999.4.7"}',
        '[{"name":"QproFaceTracking","version":"2.4.7"}]')) {
        [System.IO.File]::WriteAllText($manifestPath, $payload)
        Assert-Rejected { Get-QproModuleVersion $runtime } "Malformed packaged manifest: $payload"
    }
    [System.IO.File]::WriteAllText($manifestPath, (' ' * 65537))
    Assert-Rejected { Get-QproModuleVersion $runtime } 'Oversized manifest'

    $expectedSources = @(
        'tests\hub-action-feedback\HubActionFeedbackTests.csproj', 'tests\hub-action-feedback\Program.cs',
        'tests\hub-adb\HubAdbTests.csproj', 'tests\hub-adb\Program.cs',
        'tests\hub-compatibility\HubCompatibilityTests.csproj', 'tests\hub-compatibility\Program.cs',
        'tests\hub-lifecycle\HubLifecycleTests.csproj', 'tests\hub-lifecycle\Program.cs',
        'tests\hub-modules\HubModuleTests.csproj', 'tests\hub-modules\Program.cs',
        'tests\hub-update-apply\HubUpdateApplyTests.csproj', 'tests\hub-update-apply\Program.cs',
        'tests\hub-updates\HubUpdateTests.csproj', 'tests\hub-updates\Program.cs',
        'tests\hub-updater-lifetime\HubUpdaterLifetimeTests.csproj', 'tests\hub-updater-lifetime\Program.cs',
        'tests\hub-update-dialog\HubUpdateDialogTests.csproj', 'tests\hub-update-dialog\Program.cs',
        'tests\hub-component-updates\HubComponentUpdateTests.csproj', 'tests\hub-component-updates\Program.cs',
        'tests\check-release-zip.py', 'tests\README.md', 'tests\helper.ps1')
    $excludedSources = @('tests\hub-updates\bin\Program.cs', 'tests\hub-updates\obj\Generated.cs',
        'tests\artifacts\private.cs', 'tests\captures\capture.py', 'tests\training\train.py',
        'tests\private\private.md', 'tests\__pycache__\module.py', 'tests\hub-updates\recording.qpcap',
        'tests\hub-updates\private-metadata.json', 'tests\hub-updates\model.pt')
    foreach ($relative in @($expectedSources) + @($excludedSources)) {
        $path = Join-Path $fixture $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [System.IO.File]::WriteAllText($path, 'Synthetic fixture')
    }
    $export = @(Get-QproPublicTestSources $fixture)
    foreach ($relative in $expectedSources) { Assert-Equal $true ($export -contains $relative) "Export $relative" }
    foreach ($relative in $excludedSources) { Assert-Equal $false ($export -contains $relative) "Exclude $relative" }
    Assert-Equal $expectedSources.Count $export.Count 'Public source count'
    $actualProjects = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Directory |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.csproj' })
    $actualExport = @(Get-QproPublicTestSources $PSScriptRoot)
    foreach ($project in $actualProjects) {
        $relative = $project.FullName.Substring($PSScriptRoot.Length + 1)
        Assert-Equal $true ($actualExport -contains $relative) 'Every current regression project exported'
    }
    $tokens = $null; $parseErrors = $null
    $sourceAst = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'build-github-source.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
    $inventory = $sourceAst.Find({ param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$sourceFiles'
    }, $true)
    if ($null -eq $inventory) { throw 'Missing public source inventory.' }
    $listedSources = @($inventory.Right.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.StringConstantExpressionAst]
    }, $true) | ForEach-Object Value)
    foreach ($shared in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'shared') -File -Filter '*.cs') {
        Assert-Equal $true ($listedSources -contains ('shared\' + $shared.Name)) 'Every shared C# dependency exported'
    }
    foreach ($required in @('test_rocm_environment_isolation.ps1', 'test_vrcft_module_preflight.ps1')) {
        Assert-Equal $true ($listedSources -contains $required) "Public regression inventory: $required"
    }
    $privateSource = Join-Path $fixture 'external-private-source'
    New-Item -ItemType Directory -Path $privateSource | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $privateSource 'private.cs'), 'Private fixture must stay out of export')
    $link = Join-Path $fixture 'tests\linked-source'
    try {
        $linkCreated = $false
        try {
            New-Item -ItemType Junction -Path $link -Target $privateSource | Out-Null
            $linkCreated = $true
        } catch {
            Write-Host "SKIP: this environment cannot create the optional junction fixture ($($_.Exception.Message))."
        }
        if ($linkCreated) { Assert-Rejected { Get-QproPublicTestSources $fixture } 'Linked test source directory' }
    } finally {
        if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
    }
    Write-Host "Release metadata/source fixtures passed: $checks checks. No module was installed and no release was built."
} finally {
    $full = [System.IO.Path]::GetFullPath($fixture)
    if (-not $full.StartsWith($fixtureParent + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $full) -notmatch '^metadata-[0-9a-f]{32}$') { throw 'Unsafe metadata fixture cleanup.' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
