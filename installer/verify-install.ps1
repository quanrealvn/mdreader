<#
.SYNOPSIS
    End-to-end check of installer\output\MdReader-Setup.exe on this machine: silent per-user install into a temp folder,
    verification of every file and registry value (docs/ARCHITECTURE.md section 12), launches of the installed app,
    reinstall over a running copy, silent uninstall while a copy is running, and verification that everything Setup added
    is gone while shared keys are untouched.

.DESCRIPTION
    Uses the real per-user registry (HKCU), the real Start menu, and the real "Apps" list entry, because that's what the
    installer changes; every change is undone by the final uninstall, which the script always attempts.
    Refuses to run (exit code 2) when MdReader is already installed or leftover MdReader registration exists, so a real
    installation is never clobbered.
    Every app launch is isolated: --profile-dir <temp> --instance-id verify-<guid>. The only processes this script ends
    are ones it started (by PID); the uninstaller itself only ends MdReader.exe copies running from the temp install folder.
    Prints a PASS/FAIL table. Exit code: 0 all passed, 1 a check failed, 2 couldn't run. Windows PowerShell 5.1 compatible.

.PARAMETER Setup
    The installer to test. Default: installer\output\MdReader-Setup.exe.

.PARAMETER Document
    Markdown file used for the --capture launches. Default: docs\samples\README.md.

.PARAMETER KeepTemp
    Keep the temp folder (logs, captures, profiles) even when every check passes.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\verify-install.ps1
#>
[CmdletBinding()]
param(
    [string]$Setup,
    [string]$Document,
    [switch]$KeepTemp
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Setup) { $Setup = Join-Path $PSScriptRoot 'output\MdReader-Setup.exe' }
if (-not $Document) {
    $Document = Join-Path $RepoRoot 'docs\samples\README.md'
    if (-not (Test-Path -LiteralPath $Document)) {
        $Document = Join-Path ([IO.Path]::GetTempPath()) 'MdReaderVerify-sample.md'
        Set-Content -LiteralPath $Document -Value "# MdReader`n`nInstaller check." -Encoding UTF8
    }
}
$PublishDir = Join-Path $RepoRoot 'artifacts\publish\win-x64'

$Extensions = @('.md', '.markdown', '.mdown', '.mkd', '.mkdn')
$ProgId = 'MdReader.Markdown'
$ExeName = 'MdReader.exe'
$AppPathsKey = 'Software\Microsoft\Windows\CurrentVersion\App Paths'
$WebView2ClientKey = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$AppTimeoutMs = 90000
$SetupTimeoutMs = 300000

$Results = New-Object System.Collections.Generic.List[object]
$StartedProcesses = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$Hkcu = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)

class VerifyAbort : System.Exception {
    VerifyAbort([string]$message) : base($message) {}
}

# ---------------------------------------------------------------- results

function Add-Row([string]$Phase, [string]$Check, [string]$Result, [string]$Detail) {
    $Results.Add([pscustomobject]@{ Phase = $Phase; Check = $Check; Result = $Result; Detail = $Detail })
    $color = @{ PASS = 'Green'; FAIL = 'Red'; WARN = 'Yellow' }[$Result]
    Write-Host ("  [{0}] {1}{2}" -f $Result, $Check, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor $color
}

function Add-Result([string]$Phase, [string]$Check, [bool]$Pass, [string]$Detail = '') {
    Add-Row $Phase $Check $(if ($Pass) { 'PASS' } else { 'FAIL' }) $Detail
}

# Something outside the installer's responsibility (an app defect) that the run observed; doesn't fail the run.
function Add-Warning([string]$Phase, [string]$Check, [string]$Detail) { Add-Row $Phase $Check 'WARN' $Detail }

function Test-LogLine([string]$Phase, [string]$Check, [string]$LogPath, [string]$Pattern) {
    $match = $null
    if (Test-Path -LiteralPath $LogPath) { $match = Select-String -LiteralPath $LogPath -Pattern $Pattern | Select-Object -First 1 }
    Add-Result $Phase $Check ($null -ne $match) $(if ($match) { "log: '$($match.Line.Substring([Math]::Min(26, $match.Line.Length)).Trim())'" } else { "no line matching '$Pattern' in $LogPath" })
}

function Write-Phase([string]$Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- registry helpers (HKCU, 64-bit view)

function Test-Key([string]$SubKey) {
    $key = $Hkcu.OpenSubKey($SubKey)
    if ($null -eq $key) { return $false }
    $key.Close()
    return $true
}

# Returns $null when the value doesn't exist, else @{ Data; Kind }.
function Get-RegValue([string]$SubKey, [string]$Name) {
    $key = $Hkcu.OpenSubKey($SubKey)
    if ($null -eq $key) { return $null }
    try {
        if (-not (@($key.GetValueNames()) -contains $Name)) { return $null }
        return @{
            Data = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            Kind = $key.GetValueKind($Name)
        }
    }
    finally { $key.Close() }
}

function Format-RegData($Data) {
    if ($null -eq $Data) { return '<null>' }
    if ($Data -is [array]) { return (@($Data | ForEach-Object { "$_" }) -join ',') }
    return [string]$Data
}

function Add-KeyLines([string]$SubKey, [System.Collections.Generic.List[string]]$Lines) {
    $key = $Hkcu.OpenSubKey($SubKey)
    if ($null -eq $key) { return }
    try {
        foreach ($name in (@($key.GetValueNames()) | Sort-Object)) {
            $data = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $Lines.Add(("{0} | {1} | {2} | {3}" -f $SubKey, $name, $key.GetValueKind($name), (Format-RegData $data)))
        }
        foreach ($child in (@($key.GetSubKeyNames()) | Sort-Object)) {
            $Lines.Add("$SubKey\$child\")
            Add-KeyLines "$SubKey\$child" $Lines
        }
    }
    finally { $key.Close() }
}

# Every value and subkey below HKCU\SubKey, one line each (sorted); '<absent>' when the key doesn't exist.
function Get-KeySnapshot([string]$SubKey) {
    if (-not (Test-Key $SubKey)) { return @('<absent>') }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("$SubKey\")
    Add-KeyLines $SubKey $lines
    return $lines.ToArray()
}

function Compare-Snapshot([string[]]$Before, [string[]]$After) {
    $diff = @(Compare-Object -ReferenceObject $Before -DifferenceObject $After -CaseSensitive)
    return @($diff | ForEach-Object { '{0} {1}' -f $(if ($_.SideIndicator -eq '<=') { '-' } else { '+' }), $_.InputObject })
}

# ---------------------------------------------------------------- processes (only ever ones this script started)

$ErrorOutput = @{}   # PID -> Task<string> reading the process's stderr

function Start-Tracked([string]$FilePath, [string]$Arguments, [bool]$ShellExecute, [string]$WorkingDirectory) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $ShellExecute
    $psi.WorkingDirectory = $WorkingDirectory
    if (-not $ShellExecute) {
        # Keep crash dumps of the app out of the report (they're summarized instead); read both pipes so neither fills up.
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
    }
    $process = [System.Diagnostics.Process]::Start($psi)
    if ($null -eq $process) { throw "No process was started for '$FilePath'." }
    $StartedProcesses.Add($process)
    if (-not $ShellExecute) {
        $null = $process.StandardOutput.ReadToEndAsync()
        $ErrorOutput[$process.Id] = $process.StandardError.ReadToEndAsync()
    }
    return $process
}

# First lines of what the (exited) process wrote to stderr, on one line.
function Get-ErrorSummary([System.Diagnostics.Process]$Process) {
    $task = $ErrorOutput[$Process.Id]
    if ($null -eq $task -or -not $task.Wait(5000)) { return '' }
    $lines = @($task.Result -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 3 | ForEach-Object { $_.Trim() })
    return ($lines -join ' / ')
}

function Wait-Exit([System.Diagnostics.Process]$Process, [int]$TimeoutMs) {
    if ($Process.WaitForExit($TimeoutMs)) { $Process.WaitForExit(); return $true }   # 2nd call flushes async state
    return $false
}

function Stop-Tracked([System.Diagnostics.Process]$Process) {
    try { if (-not $Process.HasExited) { $Process.Kill(); [void]$Process.WaitForExit(10000) } }
    catch { Write-Host "  (couldn't end process $($Process.Id): $($_.Exception.Message))" -ForegroundColor Yellow }
}

function Get-ProcessPath([System.Diagnostics.Process]$Process) {
    try {
        $item = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($Process.Id)" -ErrorAction Stop
        if ($item) { return $item.ExecutablePath }
    }
    catch { }
    return $null
}

function Quote([string]$Text) { return '"' + $Text + '"' }

function Test-Png([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'missing' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $signature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($bytes.Length -lt 1024) { return "only $($bytes.Length) bytes" }
    for ($i = 0; $i -lt 8; $i++) { if ($bytes[$i] -ne $signature[$i]) { return 'not a PNG' } }
    $width = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $height = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    if ($width -le 0 -or $height -le 0) { return 'empty image' }
    return "OK ${width}x${height}, $($bytes.Length) bytes"
}

# Launches the app with --capture and records the outcome. Returns nothing; failures become FAIL rows.
function Invoke-Capture([string]$Phase, [string]$Check, [string]$FilePath, [bool]$ShellExecute, [string]$ExpectedExe) {
    $png = Join-Path $TempRoot ("capture-{0}.png" -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    $profileDir = Join-Path $TempRoot 'profile'
    $arguments = '--profile-dir {0} --instance-id verify-{1} --capture {2} {3}' -f (Quote $profileDir), $RunId, (Quote $png), (Quote $Document)
    try {
        $process = Start-Tracked $FilePath $arguments $ShellExecute $TempRoot
    }
    catch {
        Add-Result $Phase $Check $false "couldn't start: $($_.Exception.Message)"
        return
    }
    $ranFrom = Get-ProcessPath $process
    if (-not (Wait-Exit $process $AppTimeoutMs)) {
        Stop-Tracked $process
        Add-Result $Phase $Check $false "no exit within $($AppTimeoutMs / 1000) s (ended by PID $($process.Id))"
        return
    }
    $pngState = Test-Png $png
    $ok = ($process.ExitCode -eq 0) -and $pngState.StartsWith('OK')
    $detail = "exit $($process.ExitCode), PNG $pngState"
    if ($process.ExitCode -ne 0) { $detail += " $(Get-ErrorSummary $process)" }
    if ($ExpectedExe) {
        if ($ranFrom) {
            $ok = $ok -and ($ranFrom -eq $ExpectedExe)
            $detail += ", ran $ranFrom"
        }
        else { $detail += ', process path not observed (exited too fast)' }
    }
    Add-Result $Phase $Check $ok $detail
}

function Start-RunningCopy([string]$ExePath, [string]$Name) {
    $profileDir = Join-Path $TempRoot "profile-$Name"
    $arguments = '--profile-dir {0} --instance-id verify-{1}-{2} {3}' -f (Quote $profileDir), $RunId, $Name, (Quote $Document)
    $process = Start-Tracked $ExePath $arguments $false $TempRoot
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    }
    Start-Sleep -Seconds 2   # let WebView2 start and load the document
    return $process
}

# ---------------------------------------------------------------- expectations (ARCHITECTURE section 12)

function Get-ExpectedValues([string]$AppDir) {
    $exe = Join-Path $AppDir $ExeName
    $open = '"{0}" "%1"' -f $exe
    $list = New-Object System.Collections.Generic.List[object]
    $add = { param($k, $n, $d) $list.Add([pscustomobject]@{ Key = $k; Name = $n; Data = $d }) }
    & $add "Software\Classes\$ProgId" '' 'Markdown Document'
    & $add "Software\Classes\$ProgId" 'FriendlyTypeName' 'Markdown Document'
    & $add "Software\Classes\$ProgId\DefaultIcon" '' ('"{0}",0' -f $exe)
    & $add "Software\Classes\$ProgId\shell\open\command" '' $open
    foreach ($ext in $Extensions) { & $add "Software\Classes\$ext\OpenWithProgids" $ProgId '' }
    & $add "Software\Classes\Applications\$ExeName" 'FriendlyAppName' 'MdReader'
    foreach ($ext in $Extensions) { & $add "Software\Classes\Applications\$ExeName\SupportedTypes" $ext '' }
    & $add "Software\Classes\Applications\$ExeName\shell\open\command" '' $open
    & $add 'Software\MdReader\Capabilities' 'ApplicationName' 'MdReader'
    & $add 'Software\MdReader\Capabilities' 'ApplicationDescription' $null   # any non-empty text
    foreach ($ext in $Extensions) { & $add 'Software\MdReader\Capabilities\FileAssociations' $ext $ProgId }
    & $add 'Software\RegisteredApplications' 'MdReader' 'Software\MdReader\Capabilities'
    & $add "$AppPathsKey\$ExeName" '' $exe
    & $add "$AppPathsKey\$ExeName" 'Path' $AppDir
    return $list
}

# Keys that belong to MdReader alone (the uninstaller deletes them whole).
$OwnKeys = @("Software\Classes\$ProgId", "Software\Classes\Applications\$ExeName", 'Software\MdReader\Capabilities', "$AppPathsKey\$ExeName")
# Values MdReader adds to shared keys.
$OwnValues = @(@($Extensions | ForEach-Object { [pscustomobject]@{ Key = "Software\Classes\$_\OpenWithProgids"; Name = $ProgId } }) +
               [pscustomobject]@{ Key = 'Software\RegisteredApplications'; Name = 'MdReader' })
# Shared keys: must look exactly the same after uninstall as before install.
$SharedKeys = @($Extensions | ForEach-Object { "Software\Classes\$_" })
# Parent keys Setup may create: must exist after uninstall exactly when they existed before.
$ParentKeys = @(@($Extensions | ForEach-Object { "Software\Classes\$_"; "Software\Classes\$_\OpenWithProgids" }) +
                'Software\Classes\Applications', 'Software\RegisteredApplications', $AppPathsKey, 'Software\MdReader')

function Assert-Installation([string]$Phase, [string]$AppDir, [string]$ExpectedVersion, [string]$UninstallKey) {
    $exe = Join-Path $AppDir $ExeName

    # Files
    $required = @($ExeName, 'MdReader.dll', 'web\index.html', 'web\vendor\mermaid\mermaid.min.js', 'THIRD-PARTY-NOTICES.md', 'unins000.exe', 'unins000.dat')
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $AppDir $_) -PathType Leaf) })
    Add-Result $Phase 'Key files installed' ($missing.Count -eq 0) $(if ($missing.Count) { "missing: $($missing -join ', ')" } else { ($required -join ', ') })
    $loader = @('WebView2Loader.dll', 'runtimes\win-x64\native\WebView2Loader.dll' | Where-Object { Test-Path -LiteralPath (Join-Path $AppDir $_) -PathType Leaf })
    Add-Result $Phase 'WebView2Loader.dll installed' ($loader.Count -gt 0) ($loader -join ' + ')

    if (Test-Path -LiteralPath (Join-Path $PublishDir $ExeName)) {
        $published = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File)
        $bad = New-Object System.Collections.Generic.List[string]
        foreach ($file in $published) {
            $relative = $file.FullName.Substring($PublishDir.Length).TrimStart('\')
            $target = Join-Path $AppDir $relative
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { $bad.Add("missing $relative") }
            elseif ((Get-Item -LiteralPath $target).Length -ne $file.Length) { $bad.Add("size differs: $relative") }
        }
        $sameExe = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PublishDir $ExeName) -Algorithm SHA256).Hash
        if (-not $sameExe) { $bad.Add('MdReader.exe hash differs from the publish folder') }
        Add-Result $Phase 'Install folder matches the publish folder' ($bad.Count -eq 0) $(if ($bad.Count) { ($bad | Select-Object -First 5) -join '; ' } else { "$($published.Count) files, sizes equal, exe SHA-256 equal" })
    }
    else {
        Add-Result $Phase 'Install folder matches the publish folder' $true "skipped: $PublishDir not found"
    }

    # Registry values
    $wrong = New-Object System.Collections.Generic.List[string]
    $expected = Get-ExpectedValues $AppDir
    foreach ($item in $expected) {
        $actual = Get-RegValue $item.Key $item.Name
        $label = "HKCU\$($item.Key)\$(if ($item.Name) { $item.Name } else { '(Default)' })"
        if ($null -eq $actual) { $wrong.Add("$label missing"); continue }
        if ($actual.Kind -ne [Microsoft.Win32.RegistryValueKind]::String) { $wrong.Add("$label is $($actual.Kind), not REG_SZ"); continue }
        if ($null -eq $item.Data) {
            if ([string]::IsNullOrWhiteSpace([string]$actual.Data)) { $wrong.Add("$label is empty") }
        }
        elseif ([string]$actual.Data -ne $item.Data) { $wrong.Add("$label = '$($actual.Data)', expected '$($item.Data)'") }
    }
    Add-Result $Phase ("{0} registry values (ProgID, OpenWithProgids, Applications, Capabilities, RegisteredApplications, App Paths)" -f $expected.Count) `
        ($wrong.Count -eq 0) $(if ($wrong.Count) { ($wrong | Select-Object -First 4) -join '; ' } else { 'all present with the expected data' })

    # Shared .md keys: everything that was there before is still there
    $lost = New-Object System.Collections.Generic.List[string]
    foreach ($key in $SharedKeys) {
        $before = $SnapshotBefore[$key]
        if ($before.Count -eq 1 -and $before[0] -eq '<absent>') { continue }
        $now = Get-KeySnapshot $key
        foreach ($line in $before) { if (-not ($now -ccontains $line)) { $lost.Add($line) } }
    }
    Add-Result $Phase 'Existing .md/.markdown/... entries preserved' ($lost.Count -eq 0) $(if ($lost.Count) { "lost: $(($lost | Select-Object -First 3) -join '; ')" } else { 'no pre-existing value changed' })

    # "Apps" entry
    $uninstall = Get-ItemProperty -LiteralPath "HKCU:\$UninstallKey" -ErrorAction SilentlyContinue
    if ($null -eq $uninstall) {
        Add-Result $Phase 'Apps & features entry' $false "HKCU\$UninstallKey missing"
    }
    else {
        $problems = @()
        if ($uninstall.DisplayName -ne 'MdReader') { $problems += "DisplayName '$($uninstall.DisplayName)'" }
        if ($uninstall.DisplayVersion -ne $ExpectedVersion) { $problems += "DisplayVersion '$($uninstall.DisplayVersion)' (expected $ExpectedVersion)" }
        if (-not $uninstall.Publisher) { $problems += 'no Publisher' }
        if ($uninstall.DisplayIcon -ne $exe) { $problems += "DisplayIcon '$($uninstall.DisplayIcon)'" }
        if ($uninstall.InstallLocation.TrimEnd('\') -ne $AppDir) { $problems += "InstallLocation '$($uninstall.InstallLocation)'" }
        if ($uninstall.UninstallString -notlike "*$AppDir\unins000.exe*") { $problems += "UninstallString '$($uninstall.UninstallString)'" }
        Add-Result $Phase 'Apps & features entry' ($problems.Count -eq 0) $(if ($problems.Count) { $problems -join '; ' } else { "MdReader $($uninstall.DisplayVersion), publisher '$($uninstall.Publisher)'" })
    }

    # Start menu shortcut (no desktop shortcut: that task is unchecked by default)
    if (Test-Path -LiteralPath $StartMenuLink) {
        $target = (New-Object -ComObject WScript.Shell).CreateShortcut($StartMenuLink).TargetPath
        Add-Result $Phase 'Start menu shortcut' ($target -eq $exe) "$StartMenuLink -> $target"
    }
    else { Add-Result $Phase 'Start menu shortcut' $false "$StartMenuLink missing" }
    if (-not $DesktopLinkExisted) {
        Add-Result $Phase 'No desktop shortcut (task unchecked by default)' (-not (Test-Path -LiteralPath $DesktopLink)) $DesktopLink
    }
}

function Get-WebView2Version {
    foreach ($path in @("HKLM:\SOFTWARE\WOW6432Node\$WebView2ClientKey", "HKLM:\SOFTWARE\$WebView2ClientKey", "HKCU:\Software\$WebView2ClientKey")) {
        $item = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        if ($item -and ($item.PSObject.Properties.Name -contains 'pv') -and $item.pv -and $item.pv -ne '0.0.0.0') { return $item.pv }
    }
    return $null
}

function Invoke-Setup([string]$Phase, [string]$Check, [string]$LogPath, [string]$ExtraArguments) {
    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR={0} /LOG={1} {2}' -f (Quote $InstallDir), (Quote $LogPath), $ExtraArguments
    $process = Start-Tracked $Setup $arguments.Trim() $false $TempRoot
    if (-not (Wait-Exit $process $SetupTimeoutMs)) {
        Stop-Tracked $process
        Add-Result $Phase $Check $false "no exit within $($SetupTimeoutMs / 1000) s"
        return $false
    }
    $ok = $process.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $InstallDir $ExeName))
    Add-Result $Phase $Check $ok "exit $($process.ExitCode), log $LogPath"
    return $ok
}

# Runs unins000.exe silently and waits until its temp clone has removed the install folder.
function Invoke-Uninstall([string]$Phase, [string]$LogPath) {
    $uninstaller = Join-Path $InstallDir 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller)) {
        Add-Result $Phase 'Silent uninstall' $false "$uninstaller missing"
        return $false
    }
    $process = Start-Tracked $uninstaller ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG={0}' -f (Quote $LogPath)) $false $TempRoot
    $exited = Wait-Exit $process $SetupTimeoutMs
    # The uninstaller relaunches itself from %TEMP%; the clone finishes (deletes unins000.exe and the folder) after the
    # original exits, so poll for the folder to disappear.
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $deadline -and (Test-Path -LiteralPath $InstallDir)) { Start-Sleep -Milliseconds 500 }
    $gone = -not (Test-Path -LiteralPath $InstallDir)
    $exitText = if ($exited) { "exit $($process.ExitCode)" } else { 'no exit within the timeout' }
    Add-Result $Phase 'Silent uninstall (unins000.exe /VERYSILENT)' ($exited -and $process.ExitCode -eq 0) "$exitText, log $LogPath"
    return $gone
}

# ---------------------------------------------------------------- main

$exitCode = 0
$installed = $false
$uninstalled = $false
$RunStartedUtc = [DateTime]::UtcNow
$RunId = [guid]::NewGuid().ToString('N')
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) "MdReaderVerify-$($RunId.Substring(0, 12))"
$InstallDir = Join-Path $TempRoot 'app'
$StartMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'MdReader.lnk'
$DesktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'MdReader.lnk'
$DesktopLinkExisted = $false
$SnapshotBefore = @{}

try {
    Write-Phase 'Preconditions'
    if (-not (Test-Path -LiteralPath $Setup -PathType Leaf)) { throw [VerifyAbort]::new("$Setup not found. Run installer\build.ps1 first.") }
    if (-not (Test-Path -LiteralPath $Document -PathType Leaf)) { throw [VerifyAbort]::new("$Document not found.") }
    $iss = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MdReader.iss') -Raw
    if ($iss -notmatch '(?m)^AppId=\{\{([0-9A-Fa-f-]{36})\}') { throw [VerifyAbort]::new('AppId not found in MdReader.iss.') }
    $AppId = $Matches[1]
    $UninstallKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\{$AppId}_is1"
    $ExpectedVersion = ([string](Get-Item -LiteralPath $Setup).VersionInfo.ProductVersion).Trim([char]0, ' ')   # Setup pads it
    Write-Host "Setup    : $Setup ($ExpectedVersion)"
    Write-Host "Document : $Document"
    Write-Host "Temp     : $TempRoot"

    if (Test-Key $UninstallKey) {
        $existing = Get-ItemProperty -LiteralPath "HKCU:\$UninstallKey"
        throw [VerifyAbort]::new("MdReader $($existing.DisplayVersion) is already installed for this user ($($existing.InstallLocation)). " +
            'Uninstall it first (Settings > Apps); this script never touches a real installation.')
    }
    $leftovers = @($OwnKeys | Where-Object { Test-Key $_ }) +
                 @($OwnValues | Where-Object { $null -ne (Get-RegValue $_.Key $_.Name) } | ForEach-Object { "$($_.Key)\$($_.Name)" })
    if ($leftovers.Count -gt 0) {
        throw [VerifyAbort]::new("Leftover MdReader registration found (HKCU): $($leftovers -join '; '). Remove it (or uninstall MdReader) and run again.")
    }
    if (Test-Path -LiteralPath $StartMenuLink) { throw [VerifyAbort]::new("$StartMenuLink already exists; remove it and run again.") }
    $onPath = Get-Command $ExeName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { throw [VerifyAbort]::new("$ExeName is on PATH ($($onPath.Path)), so the App Paths check would be ambiguous.") }

    $DesktopLinkExisted = Test-Path -LiteralPath $DesktopLink
    foreach ($key in $SharedKeys) { $SnapshotBefore[$key] = Get-KeySnapshot $key }
    $parentBefore = @{}
    foreach ($key in $ParentKeys) { $parentBefore[$key] = Test-Key $key }
    $dataFolders = @((Join-Path $env:APPDATA 'MdReader'), (Join-Path $env:LOCALAPPDATA 'MdReader'))
    $dataBefore = @{}
    foreach ($folder in $dataFolders) { $dataBefore[$folder] = Test-Path -LiteralPath $folder }
    $webView2 = Get-WebView2Version
    Write-Host ("Pre-state recorded: {0} shared extension keys ({1} exist), WebView2 Runtime {2}" -f $SharedKeys.Count,
        @($SharedKeys | Where-Object { $SnapshotBefore[$_][0] -ne '<absent>' }).Count, $(if ($webView2) { $webView2 } else { 'not installed' }))
    New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null

    # ------------------------------------------------------------ install
    Write-Phase "Install (silent, per-user) into $InstallDir"
    $installLog = Join-Path $TempRoot 'setup.log'
    $installed = $true   # from here on, always try to uninstall
    if (-not (Invoke-Setup 'install' 'Silent install exit code 0' $installLog '')) { throw [VerifyAbort]::new('Setup failed; see the log.') }
    $appDir = $InstallDir
    Assert-Installation 'install' $appDir $ExpectedVersion $UninstallKey

    $setupLog = Get-Content -LiteralPath $installLog -Raw
    $detected = [regex]::Match($setupLog, 'Microsoft Edge WebView2 Runtime (found: version \S+|not found)')
    $agrees = $detected.Success -and (($null -ne $webView2) -eq $detected.Value.Contains('found: version'))
    Add-Result 'install' 'WebView2 Runtime check ([Code])' $agrees $(if ($detected.Success) { "setup log: '$($detected.Value)'" } else { 'no detection line in the setup log' })

    # ------------------------------------------------------------ launch the installed app
    Write-Phase 'Launch the installed app'
    Invoke-Capture 'launch' 'Installed MdReader.exe --capture (exit 0 + PNG)' (Join-Path $appDir $ExeName) $false (Join-Path $appDir $ExeName)
    Invoke-Capture 'launch' 'ShellExecute "MdReader.exe" via App Paths --capture' $ExeName $true (Join-Path $appDir $ExeName)

    # ------------------------------------------------------------ reinstall over a running copy
    Write-Phase 'Reinstall while MdReader is running (Restart Manager must close it)'
    $running = Start-RunningCopy (Join-Path $appDir $ExeName) 'upgrade'
    Add-Result 'reinstall' 'Installed app started (window shown)' (-not $running.HasExited) "PID $($running.Id)"
    $reinstallLog = Join-Path $TempRoot 'setup-reinstall.log'
    # /NORESTARTAPPLICATIONS: Restart Manager must never relaunch MdReader without the isolation arguments.
    $null = Invoke-Setup 'reinstall' 'Silent reinstall exit code 0' $reinstallLog '/NORESTARTAPPLICATIONS'
    $closed = Wait-Exit $running 15000
    Add-Result 'reinstall' 'Running copy was closed by Setup (Restart Manager)' $closed $(if ($closed) { "PID $($running.Id) ended" } else { "PID $($running.Id) still running" })
    if (-not $closed) { Stop-Tracked $running }
    elseif ($running.ExitCode -ne 0) {
        Add-Warning 'reinstall' 'App exited cleanly on Restart Manager shutdown' ("app defect, not an installer issue: exit code 0x{0:X8} {1}" -f $running.ExitCode, (Get-ErrorSummary $running))
    }
    else { Add-Result 'reinstall' 'App exited cleanly on Restart Manager shutdown' $true 'exit code 0' }
    Test-LogLine 'reinstall' 'Restart Manager detected the running app' $reinstallLog 'RestartManager found an application using one of our files'
    Test-LogLine 'reinstall' 'Uninstall log appended (upgrade keeps one uninstaller)' $reinstallLog 'Will append to existing uninstall log'
    Assert-Installation 'reinstall' $appDir $ExpectedVersion $UninstallKey

    # ------------------------------------------------------------ uninstall while running
    Write-Phase 'Uninstall (silent) while MdReader is running'
    $running = Start-RunningCopy (Join-Path $appDir $ExeName) 'uninstall'
    Add-Result 'uninstall' 'Installed app started (window shown)' (-not $running.HasExited) "PID $($running.Id)"
    $uninstallLog = Join-Path $TempRoot 'uninstall.log'
    $folderGone = Invoke-Uninstall 'uninstall' $uninstallLog
    $uninstalled = $true
    $closed = Wait-Exit $running 15000
    Add-Result 'uninstall' 'Running copy was ended by the uninstaller' $closed $(if ($closed) { 'process from the install folder ended' } else { "PID $($running.Id) still running" })
    if (-not $closed) { Stop-Tracked $running }
    Test-LogLine 'uninstall' 'Uninstaller ended only the copy in the install folder' $uninstallLog ('Ending MdReader process {0} ' -f $running.Id)
    Test-LogLine 'uninstall' 'Uninstaller reports everything removed' $uninstallLog 'Removed all\? Yes'

    $leftFiles = @()
    if (Test-Path -LiteralPath $InstallDir) { $leftFiles = @(Get-ChildItem -LiteralPath $InstallDir -Recurse -Force | ForEach-Object { $_.FullName.Substring($InstallDir.Length) }) }
    Add-Result 'uninstall' 'Install folder removed' ($folderGone -and $leftFiles.Count -eq 0) $(if ($folderGone) { "$InstallDir is gone" } else { "left: $(($leftFiles | Select-Object -First 5) -join ', ')" })

    $keysLeft = @($OwnKeys + $UninstallKey | Where-Object { Test-Key $_ })
    Add-Result 'uninstall' 'MdReader keys removed (ProgID, Applications, Capabilities, App Paths, Apps entry)' ($keysLeft.Count -eq 0) $(if ($keysLeft.Count) { "left: $($keysLeft -join '; ')" } else { "$($OwnKeys.Count + 1) keys gone" })
    $valuesLeft = @($OwnValues | Where-Object { $null -ne (Get-RegValue $_.Key $_.Name) } | ForEach-Object { "$($_.Key)\$($_.Name)" })
    Add-Result 'uninstall' 'MdReader values removed (OpenWithProgids x5, RegisteredApplications)' ($valuesLeft.Count -eq 0) $(if ($valuesLeft.Count) { "left: $($valuesLeft -join '; ')" } else { "$($OwnValues.Count) values gone" })

    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($key in $SharedKeys) {
        foreach ($line in (Compare-Snapshot $SnapshotBefore[$key] (Get-KeySnapshot $key))) { $changed.Add($line) }
    }
    Add-Result 'uninstall' 'Shared extension keys identical to before install' ($changed.Count -eq 0) $(if ($changed.Count) { ($changed | Select-Object -First 4) -join '; ' } else { "$($SharedKeys.Count) keys compared value by value" })

    $parentDiff = @($ParentKeys | Where-Object { (Test-Key $_) -ne $parentBefore[$_] } | ForEach-Object { "$_ (before: $(if ($parentBefore[$_]) { 'present' } else { 'absent' }))" })
    $createdParents = @($ParentKeys | Where-Object { -not $parentBefore[$_] })
    $parentDetail = '{0} pre-existing kept; created by Setup and removed: {1}' -f @($ParentKeys | Where-Object { $parentBefore[$_] }).Count,
        $(if ($createdParents.Count) { $createdParents -join ', ' } else { 'none' })
    Add-Result 'uninstall' 'Parent keys exist exactly as before (created ones removed)' ($parentDiff.Count -eq 0) $(if ($parentDiff.Count) { $parentDiff -join '; ' } else { $parentDetail })

    Add-Result 'uninstall' 'Start menu shortcut removed' (-not (Test-Path -LiteralPath $StartMenuLink)) $StartMenuLink
    $dataChanged = @($dataFolders | Where-Object { (Test-Path -LiteralPath $_) -ne $dataBefore[$_] })
    Add-Result 'uninstall' 'User data folders untouched (%APPDATA%\MdReader, %LOCALAPPDATA%\MdReader)' ($dataChanged.Count -eq 0) $(if ($dataChanged.Count) { "changed: $($dataChanged -join ', ')" } else { 'existence unchanged' })
}
catch [VerifyAbort] {
    Write-Host ''
    Write-Host "ABORTED: $($_.Exception.Message)" -ForegroundColor Red
    if (-not $installed) { $exitCode = 2 } else { Add-Result 'run' 'Script completed' $false $_.Exception.Message }
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    Add-Result 'run' 'Script completed' $false $_.Exception.Message
}
finally {
    # Never leave MdReader installed or a started process running.
    if ($installed -and -not $uninstalled -and (Test-Path -LiteralPath (Join-Path $InstallDir 'unins000.exe'))) {
        Write-Host 'Cleaning up: uninstalling the test installation' -ForegroundColor Yellow
        try { $null = Invoke-Uninstall 'cleanup' (Join-Path $TempRoot 'uninstall-cleanup.log') }
        catch { Write-Host "  cleanup uninstall failed: $($_.Exception.Message)" -ForegroundColor Red }
    }
    foreach ($process in $StartedProcesses) { Stop-Tracked $process }
    $Hkcu.Close()
    # The uninstaller's temp clone can't delete its own folder; it leaves %TEMP%\is-*-uninstall.tmp with a _unins-done.tmp
    # marker for a later run to clear. Remove the ones this run produced.
    Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'is-*-uninstall.tmp' -ErrorAction SilentlyContinue |
        Where-Object { $_.CreationTimeUtc -ge $RunStartedUtc -and (Test-Path -LiteralPath (Join-Path $_.FullName '_unins-done.tmp')) } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($exitCode -ne 2) {
    $failed = @($Results | Where-Object { $_.Result -eq 'FAIL' })
    $warnings = @($Results | Where-Object { $_.Result -eq 'WARN' })
    Write-Host ''
    Write-Host ($Results | Format-Table -AutoSize -Wrap Phase, Result, Check, Detail | Out-String -Width 240).TrimEnd()
    Write-Host ''
    if ($failed.Count -eq 0 -and $Results.Count -gt 0) {
        Write-Host ("VERIFY PASSED: {0} checks passed, {1} warning(s) about the app (not the installer)" -f ($Results.Count - $warnings.Count), $warnings.Count) -ForegroundColor Green
        if (-not $KeepTemp) {
            # WebView2 helper processes of the ended copies may hold their profile folders for a moment.
            for ($attempt = 0; $attempt -lt 10 -and (Test-Path -LiteralPath $TempRoot); $attempt++) {
                try { Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 1 }
            }
            if (Test-Path -LiteralPath $TempRoot) { Write-Host "Note: couldn't delete $TempRoot yet (files in use)." -ForegroundColor Yellow }
        }
        else { Write-Host "Temp folder kept: $TempRoot" }
    }
    else {
        Write-Host ("VERIFY FAILED: {0} of {1} checks failed. Logs and captures: {2}" -f $failed.Count, $Results.Count, $TempRoot) -ForegroundColor Red
        $exitCode = 1
    }
}
exit $exitCode
