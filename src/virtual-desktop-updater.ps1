[CmdletBinding()]
param(
    [ValidateSet('Status', 'Pause', 'Restore')][string]$Action = 'Status',
    [string]$ExpectedUserSid = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$script:QproVdGroup = 'Qpro Companion Streamer updater'
$script:QproVdProduct = '{B8DCEF99-DABF-40DE-B426-9680D72722E9}'

function New-QproVdStatus([string]$Title, [string]$Detail, [bool]$CanPause = $false,
    [bool]$CanRestore = $false, [bool]$IsPaused = $false, $Install = $null, [bool]$RequiresAdmin = $false) {
    [pscustomobject]@{
        Title = $Title; Detail = $Detail; CanPause = $CanPause; CanRestore = $CanRestore
        IsPaused = $IsPaused; InstallRoot = $(if ($Install) { $Install.Root } else { $null })
        StreamerVersion = $(if ($Install) { $Install.Version } else { $null }); RequiresAdmin = $RequiresAdmin
    }
}

function Assert-QproVdPlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'The Streamer updater path contains a linked file or folder. No updater control was changed.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Read-QproVdIni([string]$Text) {
    if ($Text.Length -gt 65536) { throw 'The Streamer updater configuration is too large.' }
    $section = ''; $values = @{}
    foreach ($line in ($Text -split '\r?\n')) {
        $entry = $line.Trim()
        if (!$entry -or $entry.StartsWith(';') -or $entry.StartsWith('#')) { continue }
        if ($entry -match '^\[([^\]]+)\]$') { $section = $Matches[1]; continue }
        if ($section -ne 'General') { continue }
        if ($entry -notmatch '^([^=]+)=(.*)$') { throw 'The Streamer updater configuration is malformed.' }
        $name = $Matches[1].Trim(); $value = $Matches[2].Trim()
        if ($values.ContainsKey($name)) { throw 'The Streamer updater configuration contains duplicate settings.' }
        $values[$name] = $value
    }
    foreach ($name in @('AppDir', 'ApplicationName', 'ApplicationVersion', 'ID', 'URL')) {
        if (!$values.ContainsKey($name)) { throw "The Streamer updater configuration is missing $name." }
    }
    if ($values.ApplicationName -ne 'Virtual Desktop Streamer' -or $values.ID -ne $script:QproVdProduct -or
        $values.URL -ne 'https://download.vrdesktop.net/files/updates.txt') {
        throw 'This Streamer updater configuration is not recognized. Its files were left alone.'
    }
    $values
}

function Get-QproVdInstall {
    $candidates = [Collections.Generic.List[string]]::new()
    foreach ($folder in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($folder) { $candidates.Add((Join-Path $folder 'Virtual Desktop Streamer')) }
    }
    foreach ($registryRoot in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')) {
        foreach ($key in @(Get-ChildItem -LiteralPath $registryRoot -ErrorAction SilentlyContinue)) {
            $entry = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if ($entry.DisplayName -eq 'Virtual Desktop Streamer' -and $entry.InstallLocation) {
                $candidates.Add([string]$entry.InstallLocation)
            }
        }
    }
    $found = @($candidates | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\', '/') } | Sort-Object -Unique | Where-Object {
        Test-Path -LiteralPath (Join-Path $_ 'VirtualDesktop.Streamer.exe') -PathType Leaf
    })
    if ($found.Count -eq 0) { return $null }
    if ($found.Count -ne 1) { throw 'More than one Streamer installation was found. Resolve the duplicate installation before using this control.' }
    $install = [IO.Path]::GetFullPath($found[0]).TrimEnd('\', '/')
    if ($install -notmatch '^[A-Za-z]:\\') { throw 'This control requires a Streamer installation on a local Windows drive.' }
    Assert-QproVdPlainPath $install
    $updater = Join-Path $install 'Updater.exe'
    $streamer = Join-Path $install 'VirtualDesktop.Streamer.exe'
    $iniPath = Join-Path $install 'Updater.ini'
    foreach ($file in @($updater, $streamer, $iniPath)) {
        Assert-QproVdPlainPath $file
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw 'The Streamer installation is incomplete. Repair it through its official installer.' }
    }
    $ini = Read-QproVdIni ([IO.File]::ReadAllText($iniPath))
    if ([IO.Path]::GetFullPath($ini.AppDir).TrimEnd('\', '/') -ne $install) {
        throw 'The updater configuration points to a different installation. No control was changed.'
    }
    foreach ($file in @($updater, $streamer)) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate -or
            $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -notmatch '^Virtual Desktop,? Inc\.?$') {
            throw 'The Streamer or updater signature could not be verified as Virtual Desktop, Inc. No control was changed.'
        }
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($streamer).FileVersion
    $parsed = $null; $configured = $null
    if (![version]::TryParse($version, [ref]$parsed) -or ![version]::TryParse($ini.ApplicationVersion, [ref]$configured) -or
        $parsed.ToString(3) -ne $configured.ToString(3)) {
        throw 'The Streamer files and updater configuration have different versions. Finish or repair the installation first.'
    }
    [pscustomobject]@{ Root = $install; Program = $updater; Version = $version
        Sha256 = (Get-FileHash -LiteralPath $updater -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Get-QproVdUserSid { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
function Test-QproVdAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Get-QproVdReceiptPath {
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'QproFaceTracking\streamer-updater\firewall-policy.json'
}
function Get-QproVdRuleName([string]$Program, [string]$Sid) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Sid + '|' + $Program.ToLowerInvariant())) }
    finally { $hash.Dispose() }
    'QproCompanion.VirtualDesktopUpdater.' + ([BitConverter]::ToString($bytes).Replace('-', '').Substring(0, 24).ToLowerInvariant())
}
function Get-QproVdDescription([string]$Sid) { 'Qpro Companion owned PC Streamer updater hold; Windows user ' + $Sid }

function Read-QproVdReceipt {
    $path = Get-QproVdReceiptPath
    Assert-QproVdPlainPath $path
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    if ((Get-Item -LiteralPath $path).Length -gt 16384) { throw 'The saved updater-control receipt is invalid.' }
    $record = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    $sid = Get-QproVdUserSid
    if ($record.schema -ne 1 -or $record.product -ne $script:QproVdProduct -or $record.userSid -ne $sid -or
        $record.program -isnot [string] -or ![IO.Path]::IsPathRooted($record.program) -or
        [IO.Path]::GetFileName($record.program) -ne 'Updater.exe' -or
        [IO.Path]::GetFullPath($record.program) -ne $record.program -or
        $record.updaterSha256 -notmatch '^[a-f0-9]{64}$' -or
        $record.ruleName -ne (Get-QproVdRuleName $record.program $sid)) {
        throw 'The saved updater-control receipt is not valid for this Windows user. No rule was changed.'
    }
    $record
}
function Save-QproVdReceipt($Record) {
    $path = Get-QproVdReceiptPath
    Assert-QproVdPlainPath $path
    if (Test-Path -LiteralPath $path) { throw 'An updater-control receipt already exists. Restore it before creating another hold.' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Record | ConvertTo-Json -Compress))
            $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true)
        } finally { $stream.Dispose() }
        # File.Move refuses an existing destination. A failed or interrupted
        # write cannot publish a truncated recovery receipt.
        [IO.File]::Move($temporary, $path)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
function Remove-QproVdReceipt {
    $path = Get-QproVdReceiptPath
    Assert-QproVdPlainPath $path
    if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
}

function Get-QproVdFirewallHealth {
    if ((Get-Service -Name mpssvc).Status -ne 'Running') { return 'Windows Defender Firewall is not running.' }
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore)
    if ($profiles.Count -ne 3 -or @($profiles | Where-Object { $_.Enabled -ne $true }).Count) {
        return 'Windows Defender Firewall must be enabled for all network profiles before Companion can confirm a hold.'
    }
    if (@($profiles | Where-Object { [string]$_.AllowLocalFirewallRules -eq 'False' }).Count) {
        return 'Windows policy prevents local firewall rules from taking effect.'
    }
    $null
}
function Assert-QproVdUpdaterIdle($Install) {
    $pending = @(Get-Process -ErrorAction Stop | Where-Object {
        $_.ProcessName -in @('Updater', 'VirtualDesktop.Setup', 'VirtualDesktop.Streamer.Setup')
    })
    foreach ($process in $pending) {
        $path = $null
        try { $path = $process.Path } catch { }
        if ($process.ProcessName -in @('VirtualDesktop.Setup', 'VirtualDesktop.Streamer.Setup') -or !$path -or $path -eq $Install.Program) {
            throw 'A Streamer updater or installer may already be running. Let it finish or close its window, then try again. Companion will not stop it.'
        }
    }
}
function Get-QproVdRule([string]$Name, [string]$Store = 'PersistentStore') {
    # A successful store query distinguishes absence from CIM, service and
    # permission failures. Never discard recovery evidence on a failed lookup.
    @(Get-NetFirewallRule -PolicyStore $Store -ErrorAction Stop | Where-Object Name -EQ $Name)
}
function Assert-QproVdOwnedRule($Rule, $Record) {
    if (@($Rule).Count -ne 1 -or $Rule.Name -ne $Record.ruleName -or $Rule.Group -ne $script:QproVdGroup -or
        [string]$Rule.Direction -ne 'Outbound' -or [string]$Rule.Action -ne 'Block' -or
        $Rule.Description -ne (Get-QproVdDescription $Record.userSid)) {
        throw 'The updater firewall rule no longer matches Companion ownership. It was left alone.'
    }
    $filter = @($Rule | Get-NetFirewallApplicationFilter)
    if ($filter.Count -ne 1 -or $filter[0].Program -ne $Record.program -or $filter[0].Package -notin @($null, '', 'Any')) {
        throw 'The updater firewall rule targets a different program. It was left alone.'
    }
}
function Test-QproVdAny($Value) { @($Value).Count -eq 1 -and [string]@($Value)[0] -eq 'Any' }
function Test-QproVdFullBlock($Rule) {
    if ([string]$Rule.Enabled -ne 'True' -or [string]$Rule.Profile -ne 'Any') { return $false }
    $port = @($Rule | Get-NetFirewallPortFilter)
    $address = @($Rule | Get-NetFirewallAddressFilter)
    $service = @($Rule | Get-NetFirewallServiceFilter)
    $interface = @($Rule | Get-NetFirewallInterfaceFilter)
    $type = @($Rule | Get-NetFirewallInterfaceTypeFilter)
    $security = @($Rule | Get-NetFirewallSecurityFilter)
    if ($port.Count -ne 1 -or $address.Count -ne 1 -or $service.Count -ne 1 -or
        $interface.Count -ne 1 -or $type.Count -ne 1 -or $security.Count -ne 1) { return $false }
    foreach ($value in @($port[0].Protocol, $port[0].LocalPort, $port[0].RemotePort, $port[0].IcmpType,
        $address[0].LocalAddress, $address[0].RemoteAddress, $service[0].Service,
        $interface[0].InterfaceAlias, $security[0].LocalUser, $security[0].RemoteUser, $security[0].RemoteMachine)) {
        if (!(Test-QproVdAny $value)) { return $false }
    }
    # The CIM interface/security defaults use zero (Any/NotRequired).
    if ([string]$type[0].InterfaceType -notin @('0', 'Any') -or
        [string]$security[0].Authentication -notin @('0', 'NotRequired') -or
        [string]$security[0].Encryption -notin @('0', 'NotRequired') -or
        [string]$security[0].OverrideBlockRules -ne 'False' -or
        [string]$port[0].DynamicTarget -notin @('0', 'Any')) { return $false }
    $true
}
function Get-QproVdOtherHolds([string]$Program, [string]$OwnName = '') {
    foreach ($rule in @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop | Where-Object {
        $_.Group -eq $script:QproVdGroup -and $_.Name -ne $OwnName -and
        [string]$_.Direction -eq 'Outbound' -and [string]$_.Action -eq 'Block' -and [string]$_.Enabled -eq 'True'
    })) {
        $filters = @($rule | Get-NetFirewallApplicationFilter)
        if ($filters.Count -eq 1 -and $filters[0].Program -eq $Program) { $rule }
    }
}

function Get-QproVdUpdaterStatus {
    $receipt = Read-QproVdReceipt
    $install = $null; $installProblem = $null
    try { $install = Get-QproVdInstall } catch { $installProblem = $_.Exception.Message }
    if ($receipt) {
        $rule = @(Get-QproVdRule $receipt.ruleName)
        if ($rule.Count) { Assert-QproVdOwnedRule $rule[0] $receipt }
        if ($rule.Count -eq 0) {
            if (@(Get-QproVdRule $receipt.ruleName 'ActiveStore').Count) {
                return New-QproVdStatus 'Windows still reports the saved updater hold' 'The persistent rule is absent, but Windows effective policy still contains its name. The receipt is kept. Let any Firewall operation finish, then retry Restore.' $false $true $false $install $true
            }
            return New-QproVdStatus 'Saved updater hold needs cleanup' 'The saved Companion hold has no firewall rule. Restore clears its receipt; it does not change other rules.' $false $true $false $install $false
        }
        $health = Get-QproVdFirewallHealth
        $current = @((Get-QproVdRule $receipt.ruleName 'ActiveStore'))
        $effective = $current.Count -eq 1 -and (Test-QproVdFullBlock $current[0])
        if ($effective) { Assert-QproVdOwnedRule $current[0] $receipt }
        if (!$install -or $install.Program -ne $receipt.program) {
            return New-QproVdStatus 'Previous updater hold remains' ('The saved hold targets a previous installation. Restore removes only that Companion rule. ' + $installProblem) $false $true $false $install $true
        }
        if ($health -or !$effective) {
            return New-QproVdStatus 'Updater hold is not confirmed' ($health + ' The saved rule is not confirmed effective on every profile. Restore remains available.') $false $true $false $install $true
        }
        $detail = 'A verified Companion outbound block is active for this Updater.exe. Its automatic and manual network checks are blocked. This does not control the Quest app, licensing, cached/manual installers or mandatory version requirements.'
        if (@(Get-QproVdOtherHolds $install.Program $receipt.ruleName).Count) {
            $detail += ' Another Windows account also has a Companion hold; restoring yours will leave its rule in place.'
        }
        return New-QproVdStatus 'PC Streamer updater network access paused' $detail $false $true $true $install $true
    }
    if ($installProblem) { return New-QproVdStatus 'Streamer updater is not supported' $installProblem }
    if (!$install) { return New-QproVdStatus 'Virtual Desktop Streamer not found' 'Install the official PC Streamer first, then check again.' }
    $name = Get-QproVdRuleName $install.Program (Get-QproVdUserSid)
    if (@(Get-QproVdOtherHolds $install.Program $name).Count) {
        return New-QproVdStatus 'Another updater hold remains' 'Another Windows account has a Companion rule for this updater. It affects this PC too. No duplicate hold will be added, and only its owning account can restore it through Companion.' $false $false $false $install
    }
    $health = Get-QproVdFirewallHealth
    if ($health) { return New-QproVdStatus 'Updater control is unavailable' $health $false $false $false $install }
    if (@(Get-QproVdRule $name).Count) {
        return New-QproVdStatus 'Existing updater rule needs attention' 'A rule uses the Companion name without a valid saved receipt. It was left alone; inspect Windows Defender Firewall before continuing.' $false $false $false $install
    }
    New-QproVdStatus 'Companion updater hold is off' 'Pause adds a Windows Defender Firewall outbound block for the signed PC Updater.exe. Administrator approval is required. Restore later removes only the Companion rule; other firewall rules are left alone. The headset app, cached/manual installers and Streamer licensing are separate.' $true $false $false $install $true
}

function Set-QproVdUpdaterPause {
    $status = Get-QproVdUpdaterStatus
    if ($status.IsPaused) { return }
    if (!$status.CanPause) { throw $status.Detail }
    if (!(Test-QproVdAdmin)) { throw 'Administrator approval is required to change the Streamer updater firewall rule.' }
    $install = Get-QproVdInstall
    Assert-QproVdUpdaterIdle $install
    $sid = Get-QproVdUserSid
    $record = [pscustomobject]@{ schema = 1; product = $script:QproVdProduct; userSid = $sid
        program = $install.Program; updaterSha256 = $install.Sha256; streamerVersion = $install.Version
        ruleName = Get-QproVdRuleName $install.Program $sid; createdUtc = [DateTime]::UtcNow.ToString('O') }
    Save-QproVdReceipt $record
    $attempted = $false
    try {
        if (@(Get-QproVdRule $record.ruleName).Count) { throw 'An existing updater rule appeared before creation. It was left alone.' }
        $attempted = $true
        New-NetFirewallRule -PolicyStore PersistentStore -Name $record.ruleName -DisplayName 'Qpro Companion: pause PC Streamer updater' -Group $script:QproVdGroup `
            -Description (Get-QproVdDescription $sid) -Direction Outbound -Action Block -Enabled True -Profile Any -Program $install.Program | Out-Null
        if (!(Get-QproVdUpdaterStatus).IsPaused) { throw 'Windows did not confirm the updater block as active. The attempted hold will be removed.' }
    } catch {
        if ($attempted) {
            $own = @(Get-QproVdRule $record.ruleName)
            if ($own.Count) { Assert-QproVdOwnedRule $own[0] $record; Remove-NetFirewallRule -PolicyStore PersistentStore -Name $record.ruleName }
        }
        if (!$attempted -or (@(Get-QproVdRule $record.ruleName).Count -eq 0 -and
            @(Get-QproVdRule $record.ruleName 'ActiveStore').Count -eq 0)) { Remove-QproVdReceipt }
        throw
    }
}
function Restore-QproVdUpdater {
    $receipt = Read-QproVdReceipt
    if (!$receipt) { return }
    $own = @(Get-QproVdRule $receipt.ruleName)
    if ($own.Count) {
        Assert-QproVdOwnedRule $own[0] $receipt
        if (!(Test-QproVdAdmin)) { throw 'Administrator approval is required to remove the Companion firewall rule.' }
        Remove-NetFirewallRule -PolicyStore PersistentStore -Name $receipt.ruleName
    }
    if (@(Get-QproVdRule $receipt.ruleName).Count) { throw 'Windows still reports the Companion updater rule. Its recovery receipt was kept.' }
    if (@(Get-QproVdRule $receipt.ruleName 'ActiveStore').Count) { throw 'Windows effective policy still contains the updater rule. Its recovery receipt was kept; let the Firewall operation finish, then retry Restore.' }
    Remove-QproVdReceipt
}

try {
    if ($Action -ne 'Status') {
        if (!$ExpectedUserSid -or $ExpectedUserSid -ne (Get-QproVdUserSid)) {
            throw 'Approve the change with the same Windows account that opened Companion. A different administrator account cannot own its recovery receipt.'
        }
        $mutex = [Threading.Mutex]::new($false, 'Local\QproCompanion.StreamerUpdater.' + $ExpectedUserSid)
        $locked = $false
        try {
            try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
            if (!$locked) { throw 'Another Companion updater-control action is still running.' }
            if ($Action -eq 'Pause') { Set-QproVdUpdaterPause } else { Restore-QproVdUpdater }
        } finally { if ($locked) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
    }
    $result = Get-QproVdUpdaterStatus
    [Console]::Out.WriteLine('QPRO_VD_UPDATER ' + ($result | ConvertTo-Json -Compress))
} catch {
    $result = New-QproVdStatus 'Streamer updater control needs attention' $_.Exception.Message
    [Console]::Out.WriteLine('QPRO_VD_UPDATER ' + ($result | ConvertTo-Json -Compress))
    exit 1
}
