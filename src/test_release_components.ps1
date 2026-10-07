$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-components.ps1')
$parent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts\release-component-tests'))
$fixture = Join-Path $parent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $fixture 'python-runtime') | Out-Null
try {
    foreach ($relative in @('setup-runtime.ps1', 'runtime-python.ps1', 'requirements-runtime.txt',
        'python-runtime\python.3.12.10.nupkg', 'Install-QproRocm.ps1', 'qpro_gpu.py')) {
        [IO.File]::WriteAllText((Join-Path $fixture $relative), 'fixture: ' + $relative)
    }
    $original = Get-QproReleaseComponents $fixture
    $repeat = Get-QproReleaseComponents $fixture
    foreach ($key in @('runtimeRecipe', 'rocmRecipe', 'legacyRocmRecipe')) {
        if ($original[$key] -notmatch '^[a-f0-9]{64}$' -or $original[$key] -ne $repeat[$key]) {
            throw "Recipe is not stable SHA256: $key"
        }
    }
    if ($original.rocmRecipe -eq $original.legacyRocmRecipe) { throw 'ROCm profiles must have distinct receipt identities.' }
    [IO.File]::AppendAllText((Join-Path $fixture 'qpro_gpu.py'), ' changed GPU validation')
    $gpuChange = Get-QproReleaseComponents $fixture
    if ($gpuChange.runtimeRecipe -ne $original.runtimeRecipe -or
        $gpuChange.rocmRecipe -eq $original.rocmRecipe -or
        $gpuChange.legacyRocmRecipe -eq $original.legacyRocmRecipe) { throw 'GPU changes must invalidate both ROCm receipts only.' }
    [IO.File]::AppendAllText((Join-Path $fixture 'python-runtime\python.3.12.10.nupkg'), ' changed Python')
    $pythonChange = Get-QproReleaseComponents $fixture
    if ($pythonChange.runtimeRecipe -eq $gpuChange.runtimeRecipe -or
        $pythonChange.rocmRecipe -ne $gpuChange.rocmRecipe) { throw 'Bundled Python changes must invalidate the runtime receipt.' }
    [IO.File]::Delete((Join-Path $fixture 'requirements-runtime.txt'))
    $rejected = $false
    try { $null = Get-QproReleaseComponents $fixture } catch { $rejected = $true }
    if (-not $rejected) { throw 'Missing recipe input was accepted.' }
    Write-Host 'PASS: stable component recipes, independent profiles, GPU/Python changes and missing-input refusal.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notmatch '^[a-f0-9]{32}$') { throw 'Unsafe component fixture cleanup.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
