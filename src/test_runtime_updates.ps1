param([string]$FixtureRoot = (Join-Path $PSScriptRoot 'artifacts\runtime-update-fixtures'))

$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetFullPath($FixtureRoot)) ('update-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$checks = 0
function Assert-Equal($Expected, $Actual, [string]$Context) {
    if ($Expected -ne $Actual) { throw "$Context expected '$Expected', found '$Actual'" }
    $script:checks++
}
function Assert-True([bool]$Actual, [string]$Context) { Assert-Equal $true $Actual $Context }

# This executable models just the subprocess boundary. It never imports Python,
# downloads a wheel or writes outside each private synthetic test environment.
$fakeSource = @'
using System;
using System.IO;
using System.Linq;
public static class RuntimeFixturePython {
    public static int Main(string[] args) {
        string fixture = Environment.GetEnvironmentVariable("QPRO_RUNTIME_TEST_FIXTURE");
        if (String.IsNullOrEmpty(fixture)) return 99;
        string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string joined = String.Join(" ", args);
        foreach (string name in new[] { "CUDA_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "GPU_DEVICE_ORDINAL" }) {
            if (Environment.GetEnvironmentVariable(name) != null) return 93;
        }
        if (Path.GetFileName(executable).Equals("nvidia-smi.exe", StringComparison.OrdinalIgnoreCase)) {
            if (File.Exists(Path.Combine(fixture, "nvidia"))) { Console.WriteLine("Fixture NVIDIA GPU"); return 0; }
            return 1;
        }
        File.AppendAllText(Path.Combine(fixture, "calls.txt"), executable + "\t" + joined + Environment.NewLine);
        string venv = Path.GetDirectoryName(Path.GetDirectoryName(executable));
        string backendFile = Path.Combine(venv, "backend.txt");
        string backend = File.Exists(backendFile) ? File.ReadAllText(backendFile).Trim() : "cpu";
        bool mutated = File.Exists(Path.Combine(venv, "pip-mutated.txt"));
        if (joined.Contains("-m pip")) {
            if (!args.Contains("-I") || !args.Contains("--isolated") || !args.Contains("--no-user")) return 91;
            if (Environment.GetEnvironmentVariable("PIP_CONFIG_FILE") != "nul" ||
                Environment.GetEnvironmentVariable("PIP_TARGET") != null ||
                Environment.GetEnvironmentVariable("PIP_PREFIX") != null ||
                Environment.GetEnvironmentVariable("PYTHONPATH") != null ||
                Environment.GetEnvironmentVariable("PYTHONHOME") != null) return 92;
            File.WriteAllText(Path.Combine(venv, "pip-mutated.txt"), joined);
            if (args.Contains("-r") && File.Exists(Path.Combine(fixture, "fail-requirements"))) { Console.Error.WriteLine("Synthetic requirements failure"); return 23; }
            if (joined.Contains("/cu128")) {
                if (File.Exists(Path.Combine(fixture, "fail-cuda-download"))) return 24;
                File.WriteAllText(backendFile, "cuda");
            }
            if (joined.Contains("/cpu")) File.WriteAllText(backendFile, "cpu");
            Console.WriteLine("Synthetic pip completed"); return 0;
        }
        if (joined.Contains("sys.prefix != sys.base_prefix")) return File.Exists(Path.Combine(fixture, "wrong-prefix")) ? 9 : 0;
        if (joined.Contains("import cv2,numpy,torch") && joined.Contains("torch.arange") && mutated && File.Exists(Path.Combine(fixture, "fail-final"))) return 25;
        if (joined.Contains("torch.version.cuda") && backend != "cuda") return 1;
        if (joined.Contains("device='cuda'") && mutated && File.Exists(Path.Combine(fixture, "fail-gpu"))) return 26;
        Console.WriteLine("Synthetic Python check completed"); return 0;
    }
}
'@
$fakeExe = Join-Path $fixture 'fixture-python.exe'
Add-Type -TypeDefinition $fakeSource -Language CSharp -OutputAssembly $fakeExe -OutputType ConsoleApplication
$setupSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'setup-runtime.ps1'))
$recipe = 'a' * 64
$oldRecipe = 'b' * 64
$powershell = Join-Path $PSHOME 'powershell.exe'
$savedEnvironment = @{}
$isolatedVariables = @('PIP_TARGET', 'PIP_PREFIX', 'PIP_CONFIG_FILE', 'PYTHONPATH', 'PYTHONHOME', 'CUDA_VISIBLE_DEVICES', 'HIP_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL')
foreach ($name in (@('LOCALAPPDATA', 'PATH', 'QPRO_PYTHON', 'QPRO_RUNTIME_TEST_FIXTURE') + $isolatedVariables)) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function New-Scenario([string]$Name, [string]$Backend = 'cpu', [string]$ReceiptRecipe = $oldRecipe) {
    $case = Join-Path $fixture $Name
    $source = Join-Path $case 'QproRuntime'
    $local = Join-Path $case 'LocalAppData'
    $shared = Join-Path $local 'QproFaceTracking\runtime'
    $venv = Join-Path $shared '.venv'
    New-Item -ItemType Directory -Path $source, (Join-Path $venv 'Scripts'), (Join-Path $case 'bin'), (Join-Path $case 'foreign-python') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $source 'setup-runtime.ps1'), $setupSource)
    [IO.File]::WriteAllText((Join-Path $source 'requirements-runtime.txt'), 'synthetic-dependency==1.0')
    [IO.File]::WriteAllText((Join-Path $source 'runtime-python.ps1'), 'function Test-QproPython312([string]$Python) { return (Test-Path -LiteralPath $Python -PathType Leaf) }')
    [IO.File]::WriteAllText((Join-Path $source 'release-manifest.json'), ('{"componentUpdates":{"schema":1,"runtimeRecipe":"' + $recipe + '"}}'))
    [IO.File]::Copy($fakeExe, (Join-Path $venv 'Scripts\python.exe'))
    [IO.File]::Copy($fakeExe, (Join-Path $case 'bin\nvidia-smi.exe'))
    [IO.File]::Copy($fakeExe, (Join-Path $case 'foreign-python\python.exe'))
    [IO.File]::WriteAllText((Join-Path $venv 'backend.txt'), $Backend)
    [IO.File]::WriteAllText((Join-Path $venv 'original.txt'), 'Original environment content')
    $record = @{ format = 'qpro-runtime-ready-v1'; python = (Join-Path $venv 'Scripts\python.exe'); recipeSha256 = $ReceiptRecipe; completedUtc = '2020-01-01T00:00:00Z' } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $shared 'runtime-ready.json'), $record)
    return [pscustomobject]@{ Root = $case; Source = $source; Local = $local; Shared = $shared; Venv = $venv; OriginalReceipt = $record }
}

function Run-Scenario($Scenario, [switch]$Update, [switch]$Custom, [switch]$Polluted, [switch]$ObserveRestoration) {
    $env:LOCALAPPDATA = $Scenario.Local
    $env:PATH = (Join-Path $Scenario.Root 'bin') + ';' + $savedEnvironment.PATH
    $env:QPRO_RUNTIME_TEST_FIXTURE = $Scenario.Root
    $env:QPRO_PYTHON = if ($Custom) { Join-Path $Scenario.Root 'foreign-python\python.exe' } else { $null }
    foreach ($key in $isolatedVariables) {
        [Environment]::SetEnvironmentVariable($key, $(if ($Polluted) { Join-Path $Scenario.Root ('foreign-' + $key) } else { $null }), 'Process')
    }
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Scenario.Source 'setup-runtime.ps1'))
    if ($ObserveRestoration) {
        $runner = Join-Path $Scenario.Root 'observe-environment.ps1'
        $runnerSource = @'
param([string]$SetupPath, [switch]$Update)
$code = 0
try { & $SetupPath -Update:$Update }
catch { Write-Host $_.Exception.Message; $code = 1 }
finally {
    $snapshot = @{}
    foreach ($name in @('PIP_TARGET', 'PIP_PREFIX', 'PIP_CONFIG_FILE', 'PYTHONPATH', 'PYTHONHOME', 'CUDA_VISIBLE_DEVICES', 'HIP_VISIBLE_DEVICES', 'ROCR_VISIBLE_DEVICES', 'GPU_DEVICE_ORDINAL')) {
        $snapshot[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    [IO.File]::WriteAllText((Join-Path $env:QPRO_RUNTIME_TEST_FIXTURE 'restored-environment.json'), ($snapshot | ConvertTo-Json))
}
exit $code
'@
        [IO.File]::WriteAllText($runner, $runnerSource)
        $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $runner, '-SetupPath', (Join-Path $Scenario.Source 'setup-runtime.ps1'))
    }
    if ($Update) { $arguments += '-Update' }
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $powershell @arguments 2>&1 | ForEach-Object { $_.ToString() })
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    [IO.File]::WriteAllLines((Join-Path $Scenario.Root 'output.txt'), [string[]]$output)
    return [pscustomobject]@{ ExitCode = $code; Text = ($output -join "`n"); Calls = $(if (Test-Path -LiteralPath (Join-Path $Scenario.Root 'calls.txt')) { [IO.File]::ReadAllText((Join-Path $Scenario.Root 'calls.txt')) } else { '' }) }
}

function Read-Receipt($Scenario) { return ([IO.File]::ReadAllText((Join-Path $Scenario.Shared 'runtime-ready.json')) | ConvertFrom-Json) }
function Assert-Restored($Scenario, [string]$Context) {
    Assert-Equal $Scenario.OriginalReceipt ([IO.File]::ReadAllText((Join-Path $Scenario.Shared 'runtime-ready.json'))) "$Context original receipt"
    Assert-Equal 'Original environment content' ([IO.File]::ReadAllText((Join-Path $Scenario.Venv 'original.txt'))) "$Context original file"
    Assert-Equal $false (Test-Path -LiteralPath (Join-Path $Scenario.Venv 'pip-mutated.txt')) "$Context pip changes removed"
    Assert-Equal (Get-FileHash -LiteralPath $fakeExe).Hash (Get-FileHash -LiteralPath (Join-Path $Scenario.Venv 'Scripts\python.exe')).Hash "$Context interpreter preserved"
}

try {
    $case = New-Scenario 'old-cpu-recipe'
    $result = Run-Scenario $case -Update -Custom -Polluted -ObserveRestoration
    Assert-Equal 0 $result.ExitCode 'Update succeeds under inherited external pip/Python settings'
    Assert-True ($result.Calls -match '--upgrade -r') 'Requirements upgraded'
    Assert-True ($result.Calls -match '--index-url https://download.pytorch.org/whl/cpu') 'CPU backend chosen'
    Assert-Equal $false ($result.Calls.Contains('foreign-python')) 'Custom Python never invoked by updater'
    Assert-Equal $recipe (Read-Receipt $case).recipeSha256 'New recipe certified after install'
    Assert-Equal 'cpu' (Read-Receipt $case).backend 'CPU validation recorded'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $case.Shared -Directory -Filter 'runtime-backup-*').Count 'Recovery copy removed after success'
    $restored = [IO.File]::ReadAllText((Join-Path $case.Root 'restored-environment.json')) | ConvertFrom-Json
    foreach ($key in $isolatedVariables) {
        Assert-Equal $false (Test-Path -LiteralPath (Join-Path $case.Root ('foreign-' + $key))) "Foreign $key target untouched"
        Assert-Equal (Join-Path $case.Root ('foreign-' + $key)) $restored.$key "Successful setup restores inherited $key"
    }

    $case = New-Scenario 'same-recipe-fast-path' 'cpu' $recipe
    $result = Run-Scenario $case
    Assert-Equal 0 $result.ExitCode 'Current healthy recipe reused'
    Assert-Equal $false ($result.Calls.Contains('-m pip')) 'Current healthy recipe does not reinstall'
    Assert-Equal $case.OriginalReceipt ([IO.File]::ReadAllText((Join-Path $case.Shared 'runtime-ready.json'))) 'Healthy fast path does not rewrite receipt'

    $case = New-Scenario 'old-recipe-manual-setup'
    $result = Run-Scenario $case
    Assert-Equal 0 $result.ExitCode 'Manual setup upgrades old receipt once'
    Assert-True ($result.Calls.Contains('-m pip')) 'Old receipt is not certified by imports alone'
    Assert-Equal $recipe (Read-Receipt $case).recipeSha256 'Manual setup records installed recipe'

    $case = New-Scenario 'forced-current-update' 'cpu' $recipe
    $result = Run-Scenario $case -Update
    Assert-Equal 0 $result.ExitCode 'Explicit update bypasses healthy recipe'
    Assert-True ($result.Calls.Contains('-m pip')) 'Explicit update installs requirements'

    $case = New-Scenario 'cuda-preserved-without-driver-detection' 'cuda'
    $result = Run-Scenario $case -Update
    Assert-Equal 0 $result.ExitCode 'Working CUDA preserved when driver query misses it'
    Assert-True ($result.Calls.Contains('/cu128')) 'CUDA wheel selected'
    Assert-Equal $false ($result.Calls.Contains('/whl/cpu')) 'Existing CUDA not downgraded'
    Assert-Equal 'cuda' (Read-Receipt $case).backend 'GPU tensor validation recorded'

    $case = New-Scenario 'cpu-replaced-on-nvidia'
    [IO.File]::WriteAllText((Join-Path $case.Root 'nvidia'), 'yes')
    $result = Run-Scenario $case -Update
    Assert-Equal 0 $result.ExitCode 'NVIDIA upgrade succeeds'
    Assert-True ($result.Calls -match '--force-reinstall torch>=2.7,<3 --index-url https://download.pytorch.org/whl/cu128') 'CPU wheel forced to CUDA'

    foreach ($failure in @('fail-requirements', 'fail-final', 'fail-cuda-download', 'fail-gpu')) {
        $backend = if ($failure -in @('fail-cuda-download', 'fail-gpu')) { 'cuda' } else { 'cpu' }
        $case = New-Scenario $failure $backend
        [IO.File]::WriteAllText((Join-Path $case.Root $failure), 'fail')
        $result = Run-Scenario $case -Update
        Assert-True ($result.ExitCode -ne 0) "$failure reported"
        Assert-Restored $case $failure
        Assert-True ($result.Text.Contains('previous Qpro PC runtime and its ready record were restored')) "$failure restore reported"
    }

    $case = New-Scenario 'gpu-masks-restored-on-failure' 'cuda'
    [IO.File]::WriteAllText((Join-Path $case.Root 'nvidia'), 'yes')
    [IO.File]::WriteAllText((Join-Path $case.Root 'fail-requirements'), 'fail')
    $result = Run-Scenario $case -Update -Polluted -ObserveRestoration
    Assert-True ($result.ExitCode -ne 0) 'Polluted setup failure returned'
    Assert-True ($result.Calls.Contains('-m pip')) 'Inherited GPU masks did not block private checks'
    Assert-Restored $case 'Polluted setup failure'
    $restored = [IO.File]::ReadAllText((Join-Path $case.Root 'restored-environment.json')) | ConvertFrom-Json
    foreach ($key in $isolatedVariables) {
        Assert-Equal (Join-Path $case.Root ('foreign-' + $key)) $restored.$key "Failed setup restores inherited $key"
    }

    $case = New-Scenario 'recovery-copy-failed'
    $copyLine = '[IO.File]::Copy($file.FullName, (Join-Path $savedEnvironment $file.FullName.Substring($venvRoot.Length + 1)), $false)'
    Assert-True ($setupSource.Contains($copyLine)) 'Recovery copy fault point exists'
    $faultedSource = $setupSource.Replace($copyLine, ($copyLine + '; throw "Synthetic disk copy failure"'))
    [IO.File]::WriteAllText((Join-Path $case.Source 'setup-runtime.ps1'), $faultedSource)
    $result = Run-Scenario $case -Update
    Assert-True ($result.ExitCode -ne 0) 'Recovery copy failure returned'
    Assert-Equal $false ($result.Calls.Contains('-m pip')) 'Incomplete backup cannot start install'
    Assert-Restored $case 'Recovery copy failure'

    $case = New-Scenario 'partial-runtime-bootstrap-failed'
    Remove-Item -LiteralPath (Join-Path $case.Venv 'Scripts\python.exe')
    $result = Run-Scenario $case -Update
    Assert-True ($result.ExitCode -ne 0) 'Missing-interpreter repair reports absent bundled archive'
    Assert-True ($result.Text.Contains('bundled private Python archive is missing')) 'Partial runtime repair retains original bootstrap error'
    Assert-Equal $false ($result.Text.Contains('Recovery also failed')) 'Partial runtime is a valid completed recovery copy'
    Assert-Equal $case.OriginalReceipt ([IO.File]::ReadAllText((Join-Path $case.Shared 'runtime-ready.json'))) 'Partial runtime original receipt restored'
    Assert-Equal 'Original environment content' ([IO.File]::ReadAllText((Join-Path $case.Venv 'original.txt'))) 'Partial runtime original content restored'
    Assert-Equal $false (Test-Path -LiteralPath (Join-Path $case.Venv 'Scripts\python.exe')) 'Partial runtime missing interpreter preserved exactly'
    Assert-Equal 2 @(Get-ChildItem -LiteralPath $case.Venv -File -Force -Recurse).Count 'Partial runtime original file inventory restored'

    $case = New-Scenario 'ready-receipt-write-failed'
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($setupSource, [ref]$tokens, [ref]$parseErrors)
    $writeReady = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-ReadyMarker' }, $true)
    $faultedSource = $setupSource.Remove($writeReady.Extent.StartOffset, $writeReady.Extent.EndOffset - $writeReady.Extent.StartOffset).Insert($writeReady.Extent.StartOffset, 'function Write-ReadyMarker([string]$Python, [string]$Recipe, [string]$Backend) { throw "Synthetic receipt disk failure" }')
    [IO.File]::WriteAllText((Join-Path $case.Source 'setup-runtime.ps1'), $faultedSource)
    $result = Run-Scenario $case -Update
    Assert-True ($result.ExitCode -ne 0) 'Failed certification returns failure'
    Assert-Restored $case 'Failed certification'

    $case = New-Scenario 'concurrent-setup-refused'
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $runtimeKey = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($case.Shared).ToLowerInvariant()))).Replace('-', '') }
    finally { $hasher.Dispose() }
    $heldMutex = New-Object Threading.Mutex($true, ('Local\QproRuntimeSetup-' + $runtimeKey))
    try { $result = Run-Scenario $case -Update }
    finally { $heldMutex.ReleaseMutex(); $heldMutex.Dispose() }
    Assert-True ($result.ExitCode -ne 0) 'Concurrent setup refused'
    Assert-True ($result.Text.Contains('Another Qpro PC runtime setup is already running')) 'Concurrent setup explains action'
    Assert-Equal $false ($result.Calls.Contains('-m pip')) 'Concurrent setup never installs'
    Assert-Restored $case 'Concurrent setup'

    $case = New-Scenario 'rollback-failed'
    [IO.File]::WriteAllText((Join-Path $case.Root 'fail-requirements'), 'fail')
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($setupSource, [ref]$tokens, [ref]$parseErrors)
    $restore = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Restore-QproRuntime' }, $true)
    $faultedSource = $setupSource.Remove($restore.Extent.StartOffset, $restore.Extent.EndOffset - $restore.Extent.StartOffset).Insert($restore.Extent.StartOffset, 'function Restore-QproRuntime([string]$Backup) { throw "Synthetic locked runtime" }')
    [IO.File]::WriteAllText((Join-Path $case.Source 'setup-runtime.ps1'), $faultedSource)
    $result = Run-Scenario $case -Update
    Assert-True ($result.ExitCode -ne 0) 'Failed rollback returns failure'
    Assert-True ($result.Text.Contains('Recovery also failed: Synthetic locked runtime')) 'Failed rollback has actionable message'
    $backup = @(Get-ChildItem -LiteralPath $case.Shared -Directory -Filter 'runtime-backup-*')
    Assert-Equal 1 $backup.Count 'Failed rollback retains recovery directory'
    Assert-Equal $case.OriginalReceipt ([IO.File]::ReadAllText((Join-Path $backup[0].FullName 'runtime-ready.json'))) 'Failed rollback retains original receipt'
    Assert-Equal $false (Test-Path -LiteralPath (Join-Path $case.Shared 'runtime-ready.json')) 'Failed update has no current success receipt'

    $case = New-Scenario 'foreign-prefix-refused'
    [IO.File]::WriteAllText((Join-Path $case.Root 'wrong-prefix'), 'yes')
    $result = Run-Scenario $case -Update
    Assert-True ($result.ExitCode -ne 0) 'Non-virtual/shared prefix refused'
    Assert-Equal $false ($result.Calls.Contains('-m pip')) 'Foreign prefix not modified'
    Assert-Restored $case 'Foreign prefix'

    foreach ($manifest in @('{}', '{"componentUpdates":{"schema":2,"runtimeRecipe":"a"}}', '{invalid')) {
        $case = New-Scenario ('bad-manifest-' + [Guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText((Join-Path $case.Source 'release-manifest.json'), $manifest)
        $result = Run-Scenario $case -Update
        Assert-True ($result.ExitCode -ne 0) 'Missing/invalid recipe refused for update'
        Assert-Equal $false ($result.Calls.Contains('-m pip')) 'Invalid recipe cannot install'
        Assert-Restored $case 'Invalid recipe'
    }

    Write-Host "PASS: $checks PC runtime updater checks. All subprocesses used a synthetic Python executable; no installed runtimes were changed."
    Write-Host "Fixtures retained at $fixture"
} finally {
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
}
