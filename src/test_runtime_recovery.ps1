param([string]$FixtureRoot = (Join-Path $PSScriptRoot 'artifacts\runtime-recovery-fixtures'))
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetFullPath($FixtureRoot)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$checks = 0
function Assert-True([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $script:checks++ }
function Assert-Equal($Expected, $Actual, [string]$Name) { Assert-True ($Expected -eq $Actual) "$Name expected '$Expected', got '$Actual'" }
$fakeSource = @'
using System;
using System.IO;
public static class RecoveryFixturePython {
    public static int Main(string[] args) {
        string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string venv = Path.GetDirectoryName(Path.GetDirectoryName(executable));
        string joined = String.Join(" ", args);
        if (!joined.Contains("-I -c") || !joined.Contains("import sys,pathlib,platform,cv2,numpy,torch") || !joined.Contains("is_relative_to(p)")) return 91;
        foreach (string name in new[] { "PYTHONPATH", "PYTHONHOME", "PIP_TARGET", "HIP_VISIBLE_DEVICES" }) {
            if (Environment.GetEnvironmentVariable(name) != null) return 92;
        }
        if (File.Exists(Path.Combine(venv,"fail-health"))) { Console.Error.WriteLine("Synthetic import failure"); return 23; }
        if (File.Exists(Path.Combine(venv,"fail-gpu")) && joined.Contains("device='cuda'")) return 24;
        return 0;
    }
}
'@
$exe = Join-Path $fixture 'fixture-python.exe'
Add-Type -TypeDefinition $fakeSource -Language CSharp -OutputAssembly $exe -OutputType ConsoleApplication
$scriptSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'runtime-recovery.ps1'))
$powershell = Join-Path $PSHOME 'powershell.exe'
$savedLocal = $env:LOCALAPPDATA
$savedVariables = @{}
foreach ($name in @('PYTHONPATH','PYTHONHOME','PIP_TARGET','HIP_VISIBLE_DEVICES')) { $savedVariables[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }

function New-Case([string]$Name, [string]$Backend = 'cpu') {
    $case = Join-Path $fixture $Name
    $local = Join-Path $case 'local'
    $shared = Join-Path $local 'QproFaceTracking\runtime'
    $active = Join-Path $shared '.venv'
    $backupName = 'runtime-backup-' + [Guid]::NewGuid().ToString('N')
    $backup = Join-Path $shared $backupName
    foreach ($path in @((Join-Path $active 'Scripts'),(Join-Path $backup '.venv\Scripts'))) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    [IO.File]::Copy($exe,(Join-Path $active 'Scripts\python.exe'))
    [IO.File]::Copy($exe,(Join-Path $backup '.venv\Scripts\python.exe'))
    [IO.File]::WriteAllText((Join-Path $active 'content.txt'),'current')
    [IO.File]::WriteAllText((Join-Path $backup '.venv\content.txt'),'saved')
    [IO.File]::WriteAllText((Join-Path $shared 'runtime-ready.json'),('{"format":"qpro-runtime-ready-v1","python":"' + (Join-Path $active 'Scripts\python.exe').Replace('\','\\') + '","backend":"cpu","recipeSha256":"' + ('a'*64) + '"}'))
    [IO.File]::WriteAllText((Join-Path $backup 'runtime-ready.json'),('{"format":"qpro-runtime-ready-v1","python":"' + (Join-Path $active 'Scripts\python.exe').Replace('\','\\') + '","backend":"' + $Backend + '","recipeSha256":"' + ('b'*64) + '"}'))
    $script = Join-Path $case 'runtime-recovery.ps1'
    [IO.File]::WriteAllText($script,$scriptSource)
    $sentinel = Join-Path $local 'QproFaceTracking\models\preserve.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sentinel) -Force | Out-Null
    [IO.File]::WriteAllText($sentinel,'model and options sentinel')
    $result = [pscustomobject]@{ Root=$case; Local=$local; Shared=$shared; Active=$active; Backup=$backup; BackupName=$backupName; Script=$script; Receipt=[IO.File]::ReadAllText((Join-Path $shared 'runtime-ready.json')); Sentinel=$sentinel }
    Complete-Backup $result
    return $result
}
function Complete-Backup($Case) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Case.Backup '.venv') -File -Force -Recurse)
    $bytes = [long]0; foreach ($file in $files) { $bytes += $file.Length }
    [IO.File]::WriteAllText((Join-Path $Case.Backup 'backup-complete.json'),(@{ format='qpro-runtime-backup-v1'; environment=$Case.Active; files=$files.Count; bytes=$bytes; completedUtc='2026-10-11T00:00:00Z' } | ConvertTo-Json))
}
function Run-Case($Case,[string]$Action='Restore',[string]$Name=$Case.BackupName) {
    $env:LOCALAPPDATA=$Case.Local
    foreach ($key in $savedVariables.Keys) { [Environment]::SetEnvironmentVariable($key,(Join-Path $Case.Root 'foreign'),'Process') }
    $previous=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        $output=@(& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Case.Script -Action $Action -BackupName $Name 2>&1 | ForEach-Object { $_.ToString() })
        $code=$LASTEXITCODE
    } finally { $ErrorActionPreference=$previous }
    [IO.File]::WriteAllLines((Join-Path $Case.Root ('output-'+$Action+'.txt')),[string[]]$output)
    return [pscustomobject]@{ ExitCode=$code; Text=$output -join "`n" }
}
function Inventory($Result) {
    $line=@($Result.Text.Split("`n") | Where-Object { $_.StartsWith('QPRO_RUNTIME_RECOVERY ') })
    Assert-Equal 1 $line.Count 'One structured recovery report'
    return $line[0].Substring('QPRO_RUNTIME_RECOVERY '.Length) | ConvertFrom-Json
}
function Assert-Current($Case,[string]$Name) {
    Assert-Equal 'current' ([IO.File]::ReadAllText((Join-Path $Case.Active 'content.txt'))) "$Name keeps current environment"
    Assert-Equal $Case.Receipt ([IO.File]::ReadAllText((Join-Path $Case.Shared 'runtime-ready.json'))) "$Name keeps readiness record"
    Assert-Equal 'model and options sentinel' ([IO.File]::ReadAllText($Case.Sentinel)) "$Name keeps models/options"
}
try {
    $case=New-Case 'valid-restore'
    $list=Inventory (Run-Case $case 'List')
    Assert-Equal 1 @($list.copies).Count 'Completed saved runtime discovered'
    $result=Run-Case $case
    Assert-Equal 0 $result.ExitCode 'Saved runtime restores'
    Assert-Equal $true (Inventory $result).verified 'Only a checked runtime is certified'
    Assert-Equal 'saved' ([IO.File]::ReadAllText((Join-Path $case.Active 'content.txt'))) 'Saved content promoted'
    Assert-Equal 'saved' ([IO.File]::ReadAllText((Join-Path $case.Backup '.venv\content.txt'))) 'Original saved copy retained'
    $previous=@(Get-ChildItem -LiteralPath $case.Shared -Directory -Filter 'runtime-backup-*' | Where-Object { $_.Name -ne $case.BackupName })
    Assert-Equal 1 $previous.Count 'Previous active runtime retained'
    Assert-Equal 'current' ([IO.File]::ReadAllText((Join-Path $previous[0].FullName '.venv\content.txt'))) 'Previous runtime remains usable for recovery'
    Assert-Equal 0 (Inventory (Run-Case $case 'List')).interrupted 'Completed restore does not warn as interrupted'
    Assert-Equal 'model and options sentinel' ([IO.File]::ReadAllText($case.Sentinel)) 'Models/options preserved after restore'

    foreach ($failure in @('fail-health','fail-gpu','count-mismatch','no-receipt','foreign-record')) {
        $case=New-Case $failure $(if ($failure -eq 'fail-gpu') {'cuda'} else {'cpu'})
        if ($failure -in @('fail-health','fail-gpu')) { [IO.File]::WriteAllText((Join-Path $case.Backup ('.venv\'+$failure)),'fail'); Complete-Backup $case }
        elseif ($failure -eq 'count-mismatch') { [IO.File]::WriteAllText((Join-Path $case.Backup '.venv\extra.txt'),'unexpected') }
        elseif ($failure -eq 'no-receipt') { Remove-Item -LiteralPath (Join-Path $case.Backup 'runtime-ready.json') }
        else { $record=[IO.File]::ReadAllText((Join-Path $case.Backup 'backup-complete.json')) | ConvertFrom-Json; $record.environment=Join-Path $case.Root 'foreign'; [IO.File]::WriteAllText((Join-Path $case.Backup 'backup-complete.json'),($record | ConvertTo-Json)) }
        $result=Run-Case $case
        Assert-True ($result.ExitCode -ne 0) "$failure rejected"
        Assert-Current $case $failure
    }

    $case=New-Case 'receipt-write-fails'
    $fault='Write-QproRecoveryJson $ready $backup.Receipt'
    [IO.File]::WriteAllText($case.Script,$scriptSource.Replace($fault,($fault+'; throw "Synthetic marker failure"')))
    Assert-True ((Run-Case $case).ExitCode -ne 0) 'Receipt write failure reported'
    Assert-Current $case 'Receipt write failure'
    Assert-Equal 0 (Inventory (Run-Case $case 'List')).interrupted 'Completed rollback does not warn as interrupted'

    $copyLine='[IO.File]::Copy($file.FullName, (Join-Path $staged $file.FullName.Substring($backup.Environment.Length + 1)), $false)'
    foreach ($fault in @('copy-failed','copy-interrupted','staged-inventory-mismatch')) {
        $case=New-Case $fault
        if ($fault -eq 'copy-failed') { $faulted=$scriptSource.Replace($copyLine,($copyLine+'; throw "Synthetic copy failure"')) }
        elseif ($fault -eq 'copy-interrupted') { $faulted=$scriptSource.Replace($copyLine,($copyLine+'; exit 37')) }
        else { $inventoryLine='$stagedInventory = Get-QproRecoveryInventory $staged'; $faulted=$scriptSource.Replace($inventoryLine,($inventoryLine+'; $stagedInventory.Bytes++')) }
        [IO.File]::WriteAllText($case.Script,$faulted)
        Assert-True ((Run-Case $case).ExitCode -ne 0) "$fault reported"
        Assert-Current $case $fault
        [IO.File]::WriteAllText($case.Script,$scriptSource)
        if ($fault -eq 'copy-interrupted') {
            Assert-Equal 1 (Inventory (Run-Case $case 'List')).interrupted 'Interrupted copy is discoverable'
            Assert-Equal 0 (Run-Case $case).ExitCode 'Interrupted copy can be recovered with a verified restore'
            Assert-Equal 0 (Inventory (Run-Case $case 'List')).interrupted 'Verified restore resolves interrupted copy'
        } else {
            Assert-Equal 0 @(Get-ChildItem -LiteralPath $case.Shared -Directory -Filter 'runtime-restore-*').Count "$fault leaves no failed staging tree"
            Assert-Equal 0 (Inventory (Run-Case $case 'List')).interrupted "$fault is marked handled after cleanup"
        }
    }

    foreach ($phase in @('previous-saved','runtime-restored')) {
        $case=New-Case ('interrupted-'+$phase)
        $line='$state.phase = '''+$phase+'''; Write-QproRecoveryJson $journal $state'
        Assert-True ($scriptSource.Contains($line)) "$phase interruption point exists"
        [IO.File]::WriteAllText($case.Script,$scriptSource.Replace($line,($line+'; exit 37')))
        Assert-Equal 37 (Run-Case $case).ExitCode "$phase simulates terminated helper"
        [IO.File]::WriteAllText($case.Script,$scriptSource)
        $list=Inventory (Run-Case $case 'List')
        Assert-Equal 1 $list.interrupted "$phase interruption discovered"
        Assert-Equal 2 @($list.copies).Count "$phase retains both complete recovery copies"
        Assert-Equal 0 (Run-Case $case).ExitCode "$phase can restore original saved copy"
        Assert-Equal 'saved' ([IO.File]::ReadAllText((Join-Path $case.Active 'content.txt'))) "$phase restored content"
        Assert-Equal 0 (Inventory (Run-Case $case 'List')).interrupted "$phase recovery resolves interrupted status"
    }

    $case=New-Case 'missing-active'
    Move-Item -LiteralPath $case.Active -Destination (Join-Path $case.Root 'held-current')
    Assert-Equal 0 (Run-Case $case).ExitCode 'Restore repairs an interrupted setup with no active environment'

    $case=New-Case 'bad-path'
    Assert-True ((Run-Case $case 'Restore' '..\foreign').ExitCode -ne 0) 'Outside candidate rejected'
    Assert-Current $case 'Outside candidate'

    $case=New-Case 'linked-backup'
    $external=Join-Path $case.Root 'external'
    New-Item -ItemType Directory -Path $external | Out-Null
    $linked=Join-Path $case.Backup '.venv\linked'
    New-Item -ItemType Junction -Path $linked -Target $external | Out-Null
    Assert-True ((Run-Case $case).ExitCode -ne 0) 'Junction inside backup rejected before traversal'
    Assert-Current $case 'Linked backup'
    [IO.Directory]::Delete($linked)

    $case=New-Case 'concurrent-setup'
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $key=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($case.Shared).ToLowerInvariant()))).Replace('-','') } finally { $sha.Dispose() }
    $mutex=New-Object Threading.Mutex($true,('Local\QproRuntimeSetup-'+$key))
    try { $result=Run-Case $case } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
    Assert-True ($result.ExitCode -ne 0) 'Concurrent setup blocks recovery'
    Assert-Current $case 'Concurrent setup'
    Write-Host "$checks runtime recovery checks passed. Fixtures: $fixture"
} finally {
    $env:LOCALAPPDATA=$savedLocal
    foreach ($key in $savedVariables.Keys) { [Environment]::SetEnvironmentVariable($key,$savedVariables[$key],'Process') }
}
