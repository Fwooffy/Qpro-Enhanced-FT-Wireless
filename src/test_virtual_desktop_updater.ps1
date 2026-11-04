param([string]$FixtureRoot = (Join-Path $PSScriptRoot 'artifacts\vd-updater-fixtures'))
$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$source = Join-Path $PSScriptRoot 'virtual-desktop-updater.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create('function global:' + $definition.Extent.Text.Substring('function '.Length)))
}
$script:QproVdGroup = 'Qpro Companion Streamer updater'
$script:QproVdProduct = '{B8DCEF99-DABF-40DE-B426-9680D72722E9}'
$script:Fixture = Join-Path ([IO.Path]::GetFullPath($FixtureRoot)) ([guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($script:Fixture)
$script:Checks = 0
function Assert-Equal($Expected, $Actual, [string]$Context) {
    if ($Expected -ne $Actual) { throw "$Context expected '$Expected', found '$Actual'" }
    $script:Checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Context) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Equal $true $rejected $Context
}

# All machine-facing operations below are owned fakes. Never invoke a real
# firewall cmdlet, updater executable, service or installed app from this test.
$script:Install = [pscustomobject]@{ Root = 'C:\Program Files\Virtual Desktop Streamer'
    Program = 'C:\Program Files\Virtual Desktop Streamer\Updater.exe'; Version = '1.34.25.0'; Sha256 = ('a' * 64) }
$script:Rules = @{}; $script:Created = 0; $script:Removed = [Collections.Generic.List[string]]::new()
$script:Admin = $true; $script:FirewallRunning = $true; $script:ProfilesEnabled = $true
$script:MergeAllowed = $true; $script:Effective = $true; $script:ThrowAfterCreate = $false
$script:RunningProcesses = @()
$script:ReadFailure = $false
$script:ExtraActiveRules = @{}
$script:Scope = @{ Protocol = 'Any'; LocalPort = 'Any'; RemotePort = 'Any'; IcmpType = 'Any'; DynamicTarget = 0
    LocalAddress = 'Any'; RemoteAddress = 'Any'; Service = 'Any'; InterfaceAlias = 'Any'; InterfaceType = 0
    Authentication = 0; Encryption = 0; OverrideBlockRules = $false; LocalUser = 'Any'; RemoteUser = 'Any'; RemoteMachine = 'Any' }
function global:Get-QproVdInstall { $script:Install }
function global:Get-QproVdUserSid { 'S-1-5-21-100-200-300-1001' }
function global:Test-QproVdAdmin { $script:Admin }
function global:Get-QproVdReceiptPath { Join-Path $script:Fixture 'firewall-policy.json' }
function global:Get-Service { param($Name) [pscustomobject]@{ Status = $(if ($script:FirewallRunning) { 'Running' } else { 'Stopped' }) } }
function global:Get-Process { param($Name, $ErrorAction) $script:RunningProcesses }
function global:Get-NetFirewallProfile {
    param($PolicyStore)
    foreach ($name in 'Domain', 'Private', 'Public') {
        [pscustomobject]@{ Name = $name; Enabled = $script:ProfilesEnabled
            AllowLocalFirewallRules = $(if ($script:MergeAllowed) { 'NotConfigured' } else { 'False' }) }
    }
}
function global:Get-NetFirewallRule {
    param($PolicyStore, $Name, $ErrorAction)
    if ($script:ReadFailure) { throw 'Synthetic firewall store failure' }
    if ($PolicyStore -eq 'ActiveStore' -and !$script:Effective) { return }
    if (!$Name) {
        $script:Rules.Values
        if ($PolicyStore -eq 'ActiveStore') { $script:ExtraActiveRules.Values }
    }
    elseif ($script:Rules.ContainsKey($Name)) { $script:Rules[$Name] }
}
function global:Get-NetFirewallApplicationFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)]$InputObject)
    process { [pscustomobject]@{ Program = $InputObject.Program; Package = 'Any' } }
}
function global:Get-NetFirewallPortFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)]$InputObject)
    process { [pscustomobject]@{ Protocol = $script:Scope.Protocol; LocalPort = $script:Scope.LocalPort
        RemotePort = $script:Scope.RemotePort; IcmpType = $script:Scope.IcmpType; DynamicTarget = $script:Scope.DynamicTarget } }
}
function global:Get-NetFirewallAddressFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)]$InputObject)
    process { [pscustomobject]@{ LocalAddress = $script:Scope.LocalAddress; RemoteAddress = $script:Scope.RemoteAddress } }
}
function global:Get-NetFirewallServiceFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)]$InputObject)
    process { [pscustomobject]@{ Service = $script:Scope.Service } }
}
function global:Get-NetFirewallInterfaceFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)]$InputObject)
    process { [pscustomobject]@{ InterfaceAlias = $script:Scope.InterfaceAlias } }
}
function global:Get-NetFirewallInterfaceTypeFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)]$InputObject)
    process { [pscustomobject]@{ InterfaceType = $script:Scope.InterfaceType } }
}
function global:Get-NetFirewallSecurityFilter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline = $true)]$InputObject)
    process { [pscustomobject]@{ Authentication = $script:Scope.Authentication; Encryption = $script:Scope.Encryption
        OverrideBlockRules = $script:Scope.OverrideBlockRules; LocalUser = $script:Scope.LocalUser
        RemoteUser = $script:Scope.RemoteUser; RemoteMachine = $script:Scope.RemoteMachine } }
}
function global:New-NetFirewallRule {
    param($PolicyStore, $Name, $DisplayName, $Group, $Description, $Direction, $Action, $Enabled, $Profile, $Program)
    if ($PolicyStore -ne 'PersistentStore' -or $script:Rules.ContainsKey($Name)) { throw 'Unexpected creation target' }
    $script:Rules[$Name] = [pscustomobject]@{ Name = $Name; Group = $Group; Description = $Description
        Direction = $Direction; Action = $Action; Enabled = $Enabled; Profile = $Profile; Program = $Program }
    $script:Created++
    if ($script:ThrowAfterCreate) { throw 'Synthetic post-create failure' }
    $script:Rules[$Name]
}
function global:Remove-NetFirewallRule {
    param($PolicyStore, $Name)
    if ($PolicyStore -ne 'PersistentStore' -or !$Name.StartsWith('QproCompanion.VirtualDesktopUpdater.')) { throw 'Unexpected removal target' }
    [void]$script:Rules.Remove($Name); $script:Removed.Add($Name)
}

$validIni = @"
[General]
AppDir=C:\Program Files\Virtual Desktop Streamer\
ApplicationName=Virtual Desktop Streamer
ApplicationVersion=1.34.25
ID=$script:QproVdProduct
URL=https://download.vrdesktop.net/files/updates.txt
Flags=NoDisableAutoCheck|PerMachine
"@
Assert-Equal '1.34.25' (Read-QproVdIni $validIni).ApplicationVersion 'recognized native configuration'
Assert-Rejected { Read-QproVdIni ($validIni + "`nID=$script:QproVdProduct") } 'duplicate updater identity rejected'
Assert-Rejected { Read-QproVdIni ($validIni.Replace($script:QproVdProduct, '{00000000-0000-0000-0000-000000000000}')) } 'foreign product rejected'
Assert-Rejected { Read-QproVdIni ($validIni.Replace('https://download.vrdesktop.net/files/updates.txt', 'https://example.invalid/updates')) } 'foreign update feed rejected'
Assert-Rejected { Read-QproVdIni ('x' * 65537) } 'oversized configuration rejected'

$normal = Get-QproVdUpdaterStatus
Assert-Equal $true $normal.CanPause 'normal installation offers pause'
Assert-Equal $false $normal.IsPaused 'no rule does not claim paused'
Assert-Equal $true $normal.RequiresAdmin 'new firewall hold requires approval'
$script:Admin = $false
Assert-Rejected { Set-QproVdUpdaterPause } 'unelevated writes rejected'
Assert-Equal 0 $script:Created 'unelevated action creates no rule'
$script:Admin = $true

$unrelated = [pscustomobject]@{ Name = 'Existing.Unrelated.Block'; Program = $script:Install.Program; Group = 'Other software' }
$script:Rules[$unrelated.Name] = $unrelated
Set-QproVdUpdaterPause
$paused = Get-QproVdUpdaterStatus
Assert-Equal $true $paused.IsPaused 'effective owned block confirmed'
Assert-Equal $false $paused.CanPause 'paused state avoids duplicate actions'
Assert-Equal $true $paused.CanRestore 'paused hold offers restore'
Assert-Equal 1 $script:Created 'one owned rule created'
$saved = [IO.File]::ReadAllText((Get-QproVdReceiptPath))
Set-QproVdUpdaterPause
Assert-Equal 1 $script:Created 'repeated pause is idempotent'
Assert-Equal $saved ([IO.File]::ReadAllText((Get-QproVdReceiptPath))) 'idempotent pause keeps original receipt'
$script:ProfilesEnabled = $false
Assert-Equal $false (Get-QproVdUpdaterStatus).IsPaused 'disabled profile cannot claim protection'
Assert-Equal $true (Get-QproVdUpdaterStatus).CanRestore 'disabled firewall still permits cleanup'
$script:ProfilesEnabled = $true
$script:Effective = $false
Assert-Equal $false (Get-QproVdUpdaterStatus).IsPaused 'missing effective rule cannot claim protection'
$script:Effective = $true
foreach ($field in @('Protocol', 'LocalPort', 'RemotePort', 'IcmpType', 'DynamicTarget', 'LocalAddress', 'RemoteAddress',
    'Service', 'InterfaceAlias', 'InterfaceType', 'Authentication', 'Encryption', 'OverrideBlockRules', 'LocalUser', 'RemoteUser', 'RemoteMachine')) {
    $original = $script:Scope[$field]
    $script:Scope[$field] = 'Narrowed'
    Assert-Equal $false (Get-QproVdUpdaterStatus).IsPaused "narrowed $field cannot claim full protection"
    Assert-Equal $true (Get-QproVdUpdaterStatus).CanRestore "narrowed $field retains owned recovery"
    $script:Scope[$field] = $original
}
$script:ReadFailure = $true
Assert-Rejected { Restore-QproVdUpdater } 'failed store lookup refuses restoration'
Assert-Equal $true (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'failed lookup preserves receipt'
$name = Get-QproVdRuleName $script:Install.Program (Get-QproVdUserSid)
Assert-Equal $true $script:Rules.ContainsKey($name) 'failed lookup preserves rule'
$script:ReadFailure = $false

$oldInstall = $script:Install
$script:Install = $null
Assert-Equal $false (Get-QproVdUpdaterStatus).IsPaused 'missing installation does not claim protection'
Assert-Equal $true (Get-QproVdUpdaterStatus).CanRestore 'missing installation retains recovery'
Restore-QproVdUpdater
Assert-Equal $false (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'restore removes owned receipt'
Assert-Equal $true $script:Rules.ContainsKey($unrelated.Name) 'restore preserves unrelated rule for same executable'
Assert-Equal 1 $script:Rules.Count 'only unrelated rule remains'
Restore-QproVdUpdater
Assert-Equal 1 $script:Removed.Count 'repeated restore does not remove other rules'
$script:Install = $oldInstall

$name = Get-QproVdRuleName $script:Install.Program (Get-QproVdUserSid)
$script:Rules[$name] = [pscustomobject]@{ Name = $name; Group = 'Foreign rule'; Program = $script:Install.Program }
Assert-Equal $false (Get-QproVdUpdaterStatus).CanPause 'name collision refuses replacement'
Assert-Rejected { Set-QproVdUpdaterPause } 'foreign name collision not overwritten'
Assert-Equal 'Foreign rule' $script:Rules[$name].Group 'foreign collision left intact'
[void]$script:Rules.Remove($name)

$script:Effective = $false
Assert-Rejected { Set-QproVdUpdaterPause } 'ineffective new rule rejected'
Assert-Equal $false $script:Rules.ContainsKey($name) 'ineffective attempted rule rolled back'
Assert-Equal $false (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'failed attempt clears owned receipt'
$script:Effective = $true
$script:ThrowAfterCreate = $true
Assert-Rejected { Set-QproVdUpdaterPause } 'post-create error rejected'
Assert-Equal $false $script:Rules.ContainsKey($name) 'post-create error removes only attempted owned rule'
Assert-Equal $false (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'post-create error clears receipt after cleanup'
$script:ThrowAfterCreate = $false

$script:FirewallRunning = $false
Assert-Equal $false (Get-QproVdUpdaterStatus).CanPause 'stopped firewall refuses new hold'
$script:FirewallRunning = $true
$script:MergeAllowed = $false
Assert-Equal $false (Get-QproVdUpdaterStatus).CanPause 'local-rule policy refusal surfaced'
$script:MergeAllowed = $true
$script:RunningProcesses = @([pscustomobject]@{ ProcessName = 'Updater'; Path = $script:Install.Program })
Assert-Rejected { Set-QproVdUpdaterPause } 'running target updater blocks new hold'
Assert-Equal $false (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'running updater creates no receipt'
$script:RunningProcesses = @()
$script:RunningProcesses = @([pscustomobject]@{ ProcessName = 'VirtualDesktop.Streamer.Setup'; Path = 'C:\Downloads\VirtualDesktop.Streamer.Setup.exe' })
Assert-Rejected { Set-QproVdUpdaterPause } 'official installer process blocks new hold'
$script:RunningProcesses = @()

Set-QproVdUpdaterPause
$record = Read-QproVdReceipt
$script:Rules[$record.ruleName].Program = 'C:\Different\Updater.exe'
Assert-Rejected { Restore-QproVdUpdater } 'changed program rule not removed'
Assert-Equal $true (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'ownership failure keeps recovery evidence'
$script:Rules[$record.ruleName].Program = $script:Install.Program
Restore-QproVdUpdater
Assert-Equal $true $script:Rules.ContainsKey($unrelated.Name) 'all workflows preserve unrelated rule'

$foreignName = 'QproCompanion.VirtualDesktopUpdater.foreignaccount'
$foreign = [pscustomobject]@{ Name = $foreignName; Group = $script:QproVdGroup; Description = 'another account'
    Direction = 'Outbound'; Action = 'Block'; Enabled = 'True'; Profile = 'Any'; Program = $script:Install.Program }
$script:Rules[$foreignName] = $foreign
$foreignStatus = Get-QproVdUpdaterStatus
Assert-Equal 'Another updater hold remains' $foreignStatus.Title 'cross-user machine-wide hold is visible'
Assert-Equal $false $foreignStatus.CanPause 'cross-user hold prevents duplicate hold'
Assert-Equal $false $foreignStatus.CanRestore 'foreign hold cannot be removed through this user'
Assert-Rejected { Set-QproVdUpdaterPause } 'cross-user hold refuses another pause'
Assert-Equal $true $script:Rules.ContainsKey($foreignName) 'foreign hold remains intact'
[void]$script:Rules.Remove($foreignName)
Set-QproVdUpdaterPause
$script:Rules[$foreignName] = $foreign
Assert-Equal $true ((Get-QproVdUpdaterStatus).Detail.Contains('Another Windows account')) 'own hold explains foreign hold remains after restore'
Restore-QproVdUpdater
Assert-Equal 'Another updater hold remains' (Get-QproVdUpdaterStatus).Title 'restore does not claim global normal state'
Assert-Equal $true $script:Rules.ContainsKey($foreignName) 'restore preserves another account rule'
Assert-Equal $false (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'own receipt removed after cross-user restore'
[void]$script:Rules.Remove($foreignName)
Set-QproVdUpdaterPause
$record = Read-QproVdReceipt
$script:ExtraActiveRules[$record.ruleName] = $script:Rules[$record.ruleName]
[void]$script:Rules.Remove($record.ruleName)
Assert-Rejected { Restore-QproVdUpdater } 'remaining effective rule prevents receipt deletion'
Assert-Equal $true (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'remaining effective rule preserves recovery'
Assert-Equal $true (Get-QproVdUpdaterStatus).RequiresAdmin 'remaining effective rule does not claim unprivileged cleanup'
$script:ExtraActiveRules.Clear()
Restore-QproVdUpdater
Assert-Equal $false (Test-Path -LiteralPath (Get-QproVdReceiptPath)) 'confirmed effective absence clears receipt'
Write-Output "Virtual Desktop updater fixtures passed: $script:Checks checks. No installed updater, firewall rule, service or headset was changed."
