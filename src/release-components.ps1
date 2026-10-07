# Component receipts describe the install recipe shipped in a release. They
# are not a claim that upstream package indexes have stopped changing.
function Get-QproComponentRecipe([string]$RuntimeRoot, [string]$Component, [string[]]$Files) {
    $lines = @("qpro-component-recipe-v1:$Component")
    foreach ($relative in $Files) {
        $file = Join-Path $RuntimeRoot $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "Cannot record the $Component update recipe: missing $relative"
        }
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines += $relative.Replace('\', '/') + ':' + $hash
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-QproReleaseComponents([string]$RuntimeRoot) {
    $runtime = @('setup-runtime.ps1', 'runtime-python.ps1', 'requirements-runtime.txt',
        'python-runtime\python.3.12.10.nupkg')
    $rocm = @('Install-QproRocm.ps1', 'runtime-python.ps1', 'requirements-runtime.txt', 'qpro_gpu.py')
    return [ordered]@{
        schema = 1
        runtimeRecipe = Get-QproComponentRecipe $RuntimeRoot 'runtime' $runtime
        rocmRecipe = Get-QproComponentRecipe $RuntimeRoot 'rocm' $rocm
        legacyRocmRecipe = Get-QproComponentRecipe $RuntimeRoot 'legacy-rocm' $rocm
    }
}
