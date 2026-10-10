param([string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts\rocm-isolation-fixtures'))

$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-QproRocm.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Enter-QproRocmEnvironmentScope', 'Exit-QproRocmEnvironmentScope', 'Invoke-QproRocmPip',
    'Get-QproRocmPipFailure', 'Invoke-QproRocmPythonProbe')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $definition) { throw "The installer has no isolation helper: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$wrapper = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and $null -ne $node.Finally -and
        $node.Finally.Extent.Text.Contains('Exit-QproRocmEnvironmentScope $qproSetupEnvironment')
}, $true)
if ($null -eq $wrapper -or -not $wrapper.Body.Extent.Text.Contains('Test-QproPython312 $qproPrivatePython')) {
    throw 'The installer must enter isolation before its first Python probe and restore it in finally.'
}

# A real child executable checks the inherited environment and native argv.
# It never runs Python, pip, downloads, or writes to an installed environment.
$fixture = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('fixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$python = Join-Path $fixture 'fixture-python.exe'
$source = @'
using System;
using System.IO;
using System.Linq;
public static class RocmIsolationFixture {
    public static int Main(string[] args) {
        string root = Environment.GetEnvironmentVariable("QPRO_ROCM_ISOLATION_FIXTURE");
        if (String.IsNullOrEmpty(root)) return 99;
        foreach (string name in new[] { "PIP_TARGET", "PIP_PREFIX", "PIP_USER", "PIP_INDEX_URL", "PIP_EXTRA_INDEX_URL",
            "PYTHONHOME", "PYTHONUSERBASE", "CUDA_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "GPU_DEVICE_ORDINAL" }) {
            if (Environment.GetEnvironmentVariable(name) != null) { Console.Error.WriteLine("Inherited " + name); return 91; }
        }
        if (Environment.GetEnvironmentVariable("PIP_CONFIG_FILE") != "nul" ||
            Environment.GetEnvironmentVariable("PYTHONNOUSERSITE") != "1") return 92;
        bool pip = args.Contains("pip");
        if (pip && (!args.Contains("-I") || !args.Contains("--isolated") || !args.Contains("--no-user"))) return 93;
        if (!pip && Environment.GetEnvironmentVariable("PYTHONPATH") != root) return 94;
        File.AppendAllText(Path.Combine(root, "calls.txt"), String.Join(" ", args) + Environment.NewLine);
        if (args.Contains("--fixture-fail")) { Console.Error.WriteLine("Synthetic download failure"); return 23; }
        Console.WriteLine("Fixture subprocess passed"); return 0;
    }
}
'@
Add-Type -TypeDefinition $source -OutputAssembly $python -OutputType ConsoleApplication
$names = @('PIP_TARGET', 'PIP_PREFIX', 'PIP_USER', 'PIP_INDEX_URL', 'PIP_EXTRA_INDEX_URL', 'PIP_CONFIG_FILE',
    'PYTHONPATH', 'PYTHONHOME', 'PYTHONUSERBASE', 'PYTHONNOUSERSITE', 'HIP_VISIBLE_DEVICES', 'CUDA_VISIBLE_DEVICES',
    'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL', 'ROCM_SDK_TARGET_FAMILY', 'QPRO_ROCM_EXPECTED_GFX_TARGET', 'QPRO_ROCM_INSTALL_SMOKE_TEST')
$original = @{}
foreach ($name in ($names + @('QPRO_ROCM_ISOLATION_FIXTURE'))) {
    $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$checks = 0
function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
    $script:checks++
}
try {
    $env:QPRO_ROCM_ISOLATION_FIXTURE = $fixture
    foreach ($failure in @($false, $true)) {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, 'fixture-parent-value', 'Process') }
        $saved = Enter-QproRocmEnvironmentScope
        $caught = $false
        try {
            $env:PYTHONPATH = $fixture
            $env:ROCM_SDK_TARGET_FAMILY = 'fixture-qpro-family'
            $env:QPRO_ROCM_EXPECTED_GFX_TARGET = 'gfx1100'
            $env:QPRO_ROCM_INSTALL_SMOKE_TEST = '1'
            $probe = Invoke-QproRocmPythonProbe $python 'fixture probe'
            Assert-True ($probe.ExitCode -eq 0) 'A polluted parent affected the isolated probe.'
            $pipArguments = @('install', 'fixture-wheel')
            if ($failure) { $pipArguments += '--fixture-fail' }
            Invoke-QproRocmPip $python $pipArguments 'Fixture install failed.' $fixture
        } catch {
            if (-not $failure) { throw }
            Assert-True ($_.Exception.Message.Contains('Fixture install failed.')) 'Subprocess failure lost its useful error.'
            $caught = $true
        } finally { Exit-QproRocmEnvironmentScope $saved }
        Assert-True ($caught -eq $failure) 'Failed pip was accepted, or successful pip was rejected.'
        foreach ($name in $names) {
            Assert-True ([Environment]::GetEnvironmentVariable($name, 'Process') -eq 'fixture-parent-value') "The parent $name was not restored."
        }
    }
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    $saved = Enter-QproRocmEnvironmentScope
    try {
        $env:PYTHONPATH = $fixture
        Invoke-QproRocmPip $python @('install', 'fixture-wheel') 'Fixture install failed.' $fixture
    } finally { Exit-QproRocmEnvironmentScope $saved }
    foreach ($name in $names) { Assert-True ($null -eq [Environment]::GetEnvironmentVariable($name, 'Process')) "A new $name leaked into the parent." }
    Assert-True (([IO.File]::ReadAllLines((Join-Path $fixture 'calls.txt'))).Count -eq 5) 'A fixture subprocess did not run.'
    Write-Host "PASS: $checks ROCm environment isolation checks; no real packages or runtimes changed. Fixtures: $fixture"
} finally {
    foreach ($name in $original.Keys) { [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process') }
}
