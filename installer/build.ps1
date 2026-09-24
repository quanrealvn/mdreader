<#
.SYNOPSIS
    Builds the MdReader installer: (tests) -> self-contained ReadyToRun publish -> Inno Setup -> installer\output\MdReader-Setup.exe.

.DESCRIPTION
    1. Locates dotnet (PATH, DOTNET_ROOT, Program Files) and ISCC.exe (ISCC env var, PATH, per-user and per-machine
       Inno Setup 6 folders, Inno Setup's uninstall registration).
    2. Unless -SkipTests: dotnet test tests\MdReader.Core.Tests -c Release.
    3. Cleans artifacts\publish\win-x64 and publishes the shell (Release, win-x64, self-contained, ReadyToRun):
       src\MdReader.Ui (Avalonia) by default, src\MdReader.App (WPF) with -Wpf. Both publish as MdReader.exe.
    4. Sanity-checks the publish folder (exe, runtime, web assets, WebView2Loader.dll, file version, ReadyToRun code,
       and the assemblies the chosen shell needs: Avalonia plus its Skia/HarfBuzz/ANGLE natives, or WPF).
    5. Compiles installer\MdReader.iss and prints the setup's size and SHA-256.
    Stops at the first failure with a message and a non-zero exit code. Windows PowerShell 5.1 compatible.

.PARAMETER Version
    Product version (major.minor.patch[.revision], numbers only). Defaults to <Version> in Directory.Build.props.

.PARAMETER SkipTests
    Skip the Core unit tests.

.PARAMETER Wpf
    Build the installer around the WPF shell (src\MdReader.App) instead of the Avalonia one. The output path and file
    name don't change, so the result is a drop-in comparison build, not a second product. Only for parity checks.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.1 -SkipTests
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1 -Wpf -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$Wpf
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$PublishDir = Join-Path $RepoRoot 'artifacts\publish\win-x64'
# The shipped shell is the Avalonia one; the WPF project stays in the repo as the parity reference and can still be
# wrapped in an installer with -Wpf. Both projects publish as MdReader.exe, so everything downstream is identical.
$ShellName = if ($Wpf) { 'WPF (src\MdReader.App)' } else { 'Avalonia (src\MdReader.Ui)' }
$AppProject = if ($Wpf) { Join-Path $RepoRoot 'src\MdReader.App\MdReader.App.csproj' }
              else { Join-Path $RepoRoot 'src\MdReader.Ui\MdReader.Ui.csproj' }
# MdReader.Ui multi-targets (net10.0 is the macOS build), so the framework is named explicitly; MdReader.App has the
# same one, which keeps the publish command the same for both.
$TargetFramework = 'net10.0-windows'
$TestProject = Join-Path $RepoRoot 'tests\MdReader.Core.Tests\MdReader.Core.Tests.csproj'
$IssFile = Join-Path $PSScriptRoot 'MdReader.iss'
$OutputDir = Join-Path $PSScriptRoot 'output'
$SetupExe = Join-Path $OutputDir 'MdReader-Setup.exe'
$IsccLog = Join-Path $OutputDir 'iscc.log'

class BuildFailure : System.Exception {
    BuildFailure([string]$message) : base($message) {}
}

function Fail([string]$Message) { throw [BuildFailure]::new($Message) }

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

# Runs a native command with live output. Stderr isn't redirected, so Windows PowerShell 5.1 doesn't turn it into
# (terminating) error records; success is decided by the exit code alone.
function Invoke-Native([string]$FilePath, [string[]]$Arguments, [string]$What) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @Arguments
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { Fail "$What failed (exit code $code)." }
}

function Resolve-Version([string]$Requested) {
    if ([string]::IsNullOrWhiteSpace($Requested)) {
        $propsPath = Join-Path $RepoRoot 'Directory.Build.props'
        $props = [xml](Get-Content -LiteralPath $propsPath -Raw)
        $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
        if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
            Fail "No -Version given and Directory.Build.props has no <Version>."
        }
        $Requested = $node.InnerText.Trim()
    }
    if ($Requested -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        Fail "Version '$Requested' isn't valid: use major.minor.patch[.revision] with numbers only (Windows file versions can't carry a pre-release label)."
    }
    foreach ($part in $Requested.Split('.')) {
        if ([int64]$part -gt 65535) { Fail "Version '$Requested' isn't valid: each part must be 65535 or less." }
    }
    return $Requested
}

function Find-Dotnet {
    $candidates = New-Object System.Collections.Generic.List[string]
    $onPath = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates.Add($onPath.Path) }
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    if ($env:ProgramW6432) { $candidates.Add((Join-Path $env:ProgramW6432 'dotnet\dotnet.exe')) }
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }
    $candidates.Add('C:\Program Files\dotnet\dotnet.exe')

    $tried = @()
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $tried += $candidate
        # Run from the repo root so global.json's SDK pin applies: the candidate must have that SDK.
        Push-Location $RepoRoot
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { $sdk = & $candidate --version; $code = $LASTEXITCODE }
        finally { $ErrorActionPreference = $previous; Pop-Location }
        if ($code -eq 0 -and $sdk) {
            return [pscustomobject]@{ Path = $candidate; Sdk = ([string]($sdk | Select-Object -Last 1)).Trim() }
        }
    }
    if ($tried.Count -gt 0) {
        Fail ("dotnet was found ({0}) but none of them has the SDK that global.json requires. Install the .NET SDK listed in global.json." -f ($tried -join ', '))
    }
    Fail 'dotnet.exe wasn''t found (PATH, DOTNET_ROOT, Program Files\dotnet). Install the .NET SDK listed in global.json.'
}

function Find-Iscc {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:ISCC) { $candidates.Add($env:ISCC) }
    $onPath = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates.Add($onPath.Path) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) }
    foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:ProgramFiles)) {
        if ($root) { $candidates.Add((Join-Path $root 'Inno Setup 6\ISCC.exe')) }
    }
    # Inno Setup's own uninstall registration knows where it was installed (per-user or per-machine).
    foreach ($key in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                       'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                       'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
        $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($entry -and ($entry.PSObject.Properties.Name -contains 'InstallLocation') -and $entry.InstallLocation) {
            $candidates.Add((Join-Path $entry.InstallLocation 'ISCC.exe'))
        }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    Fail 'ISCC.exe (Inno Setup 6.6 or later) wasn''t found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php or set the ISCC environment variable to ISCC.exe.'
}

# True when the assembly carries ReadyToRun native code (its CLI header's ManagedNativeHeader points at an 'RTR' header).
function Test-ReadyToRun([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) { return $false }
    $coff = $pe + 4
    $sectionCount = [BitConverter]::ToUInt16($bytes, $coff + 2)
    $optionalSize = [BitConverter]::ToUInt16($bytes, $coff + 16)
    $optional = $coff + 20
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    if ($magic -eq 0x20B) { $directories = $optional + 112 } else { $directories = $optional + 96 }
    $sections = $optional + $optionalSize

    $toOffset = {
        param([uint32]$Rva)
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $s = $sections + 40 * $i
            $virtualSize = [BitConverter]::ToUInt32($bytes, $s + 8)
            $virtualAddress = [BitConverter]::ToUInt32($bytes, $s + 12)
            $rawSize = [BitConverter]::ToUInt32($bytes, $s + 16)
            $rawPointer = [BitConverter]::ToUInt32($bytes, $s + 20)
            $span = [Math]::Max($virtualSize, $rawSize)
            if ($Rva -ge $virtualAddress -and $Rva -lt ($virtualAddress + $span)) { return [int]($Rva - $virtualAddress + $rawPointer) }
        }
        return -1
    }

    $cliRva = [BitConverter]::ToUInt32($bytes, $directories + 14 * 8)   # IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR
    if ($cliRva -eq 0) { return $false }
    $cli = & $toOffset $cliRva
    if ($cli -lt 0) { return $false }
    $nativeHeaderRva = [BitConverter]::ToUInt32($bytes, $cli + 64)       # IMAGE_COR20_HEADER.ManagedNativeHeader
    if ($nativeHeaderRva -eq 0) { return $false }
    $nativeHeader = & $toOffset $nativeHeaderRva
    return ($nativeHeader -ge 0 -and [BitConverter]::ToUInt32($bytes, $nativeHeader) -eq 0x00525452)   # 'RTR'
}

# True when the file contains this ASCII text. Used to find an embedded resource name in an assembly without loading it.
function Test-ContainsText([string]$Path, [string]$Text) {
    $content = [IO.File]::ReadAllText($Path, [Text.Encoding]::ASCII)
    return $content.Contains($Text)
}

function Assert-PublishOutput([string]$Folder, [string]$ExpectedVersion) {
    $required = @(
        'MdReader.exe', 'MdReader.dll', 'MdReader.Core.dll', 'MdReader.Shell.dll', 'MdReader.Edge.dll',
        'MdReader.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll',
        'Microsoft.Web.WebView2.Core.dll', 'Markdig.dll', 'HtmlSanitizer.dll',
        'web\index.html', 'web\vendor\mermaid\mermaid.min.js', 'web\vendor\katex\katex.min.js', 'web\vendor\highlight\highlight.min.js'
    )
    if ($Wpf) {
        # The WPF shell draws with the Windows Presentation Foundation assemblies and hosts WebView2 in its WPF wrapper.
        $required += 'PresentationFramework.dll', 'PresentationCore.dll', 'Microsoft.Web.WebView2.Wpf.dll'
    }
    else {
        # The Avalonia shell draws with Skia: the managed bindings and the three natives (Skia, HarfBuzz for text
        # shaping, ANGLE for the GPU path) must all be beside the exe or the first window never appears.
        $required += 'Avalonia.Base.dll', 'Avalonia.Controls.dll', 'Avalonia.Desktop.dll', 'Avalonia.Win32.dll',
                     'Avalonia.Skia.dll', 'Avalonia.Themes.Fluent.dll', 'Avalonia.Markup.Xaml.dll',
                     'SkiaSharp.dll', 'HarfBuzzSharp.dll',
                     'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll'
    }
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Folder $_) -PathType Leaf) })
    if ($missing.Count -gt 0) { Fail ("The publish folder is missing: {0}" -f ($missing -join ', ')) }

    # Which shell was published is a property of the payload, not of the switch: catch a stale publish folder.
    $wpfPresent = Test-Path -LiteralPath (Join-Path $Folder 'PresentationFramework.dll') -PathType Leaf
    if ($Wpf.IsPresent -ne $wpfPresent) {
        Fail ("The publish folder holds the {0} shell, but {1} was published." -f `
            $(if ($wpfPresent) { 'WPF' } else { 'Avalonia' }), $ShellName)
    }

    $loaders = @('WebView2Loader.dll', 'runtimes\win-x64\native\WebView2Loader.dll' |
        Where-Object { Test-Path -LiteralPath (Join-Path $Folder $_) -PathType Leaf })
    if ($loaders.Count -eq 0) { Fail 'WebView2Loader.dll isn''t in the publish folder (neither beside MdReader.exe nor under runtimes\win-x64\native).' }

    # The Avalonia shell's compiled XAML, themes and window icon are an embedded resource of MdReader.dll, not loose
    # files, so a publish that dropped them would still look complete on disk.
    if (-not $Wpf) {
        if (-not (Test-ContainsText (Join-Path $Folder 'MdReader.dll') '!AvaloniaResources')) {
            Fail 'MdReader.dll carries no !AvaloniaResources section: the compiled XAML and the window icon are missing.'
        }
    }

    $runtimeConfig = Get-Content -LiteralPath (Join-Path $Folder 'MdReader.runtimeconfig.json') -Raw
    if ($runtimeConfig -notmatch '"includedFrameworks"') { Fail 'MdReader.runtimeconfig.json has no includedFrameworks: the publish isn''t self-contained.' }

    $parts = @($ExpectedVersion.Split('.'))
    while ($parts.Count -lt 4) { $parts += '0' }
    $expectedFileVersion = $parts -join '.'
    $fileVersion = (Get-Item -LiteralPath (Join-Path $Folder 'MdReader.exe')).VersionInfo.FileVersion
    if ($fileVersion -ne $expectedFileVersion) { Fail "MdReader.exe has file version '$fileVersion', expected '$expectedFileVersion'." }

    $readyToRun = @('MdReader.dll', 'MdReader.Core.dll', 'MdReader.Shell.dll')
    if (-not $Wpf) { $readyToRun += 'Avalonia.Base.dll' }
    foreach ($assembly in $readyToRun) {
        if (-not (Test-ReadyToRun (Join-Path $Folder $assembly))) { Fail "$assembly has no ReadyToRun code (PublishReadyToRun didn't take effect)." }
    }

    $files = @(Get-ChildItem -LiteralPath $Folder -Recurse -File)
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Publish folder OK: {0} shell, {1} files, {2:N1} MB, file version {3}, ReadyToRun, WebView2Loader.dll at {4}" -f `
        $(if ($Wpf) { 'WPF' } else { 'Avalonia' }), $files.Count, ($bytes / 1MB), $fileVersion, ($loaders -join ' + '))
}

$exitCode = 0
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
try {
    Write-Step 'Tools'
    $Version = Resolve-Version $Version
    $dotnet = Find-Dotnet
    $iscc = Find-Iscc
    Write-Host "Version : $Version"
    Write-Host "Shell   : $ShellName"
    Write-Host "dotnet  : $($dotnet.Path) (SDK $($dotnet.Sdk))"
    Write-Host "ISCC    : $iscc"

    Push-Location $RepoRoot
    try {
        if ($SkipTests -or -not (Test-Path -LiteralPath $TestProject)) {
            Write-Step 'Tests skipped'
        }
        else {
            Write-Step 'dotnet test tests\MdReader.Core.Tests -c Release'
            Invoke-Native $dotnet.Path @('test', $TestProject, '-c', 'Release', '--nologo') 'The Core tests'
        }

        Write-Step "dotnet publish $ShellName (Release, win-x64, self-contained, ReadyToRun) -> $PublishDir"
        if (Test-Path -LiteralPath $PublishDir) {
            try { Remove-Item -LiteralPath $PublishDir -Recurse -Force }
            catch { Fail "Couldn't clean $PublishDir (is MdReader.exe running from it?): $($_.Exception.Message)" }
        }
        Invoke-Native $dotnet.Path @('publish', $AppProject, '-c', 'Release', '-f', $TargetFramework, '-r', 'win-x64',
            '--self-contained', 'true', '-p:PublishReadyToRun=true', "-p:Version=$Version", '-o', $PublishDir, '--nologo') 'dotnet publish'

        Write-Step 'Checking the publish folder'
        Assert-PublishOutput $PublishDir $Version

        Write-Step "Compiling $IssFile"
        New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
        if (Test-Path -LiteralPath $SetupExe) { Remove-Item -LiteralPath $SetupExe -Force }
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            # Full compiler output goes to iscc.log; the console gets warnings, errors and the result.
            & $iscc "/DMyAppVersion=$Version" "/DPublishDir=$PublishDir" $IssFile |
                Tee-Object -FilePath $IsccLog |
                Where-Object { $_ -match '^\s*(Warning|Error)|Successful compile|^Compile aborted' }
            $code = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previous }
        if ($code -ne 0) { Fail "ISCC failed (exit code $code). Full output: $IsccLog" }
        if (-not (Test-Path -LiteralPath $SetupExe -PathType Leaf)) { Fail "ISCC succeeded but $SetupExe doesn't exist." }
    }
    finally {
        Pop-Location
    }

    $setup = Get-Item -LiteralPath $SetupExe
    $hash = (Get-FileHash -LiteralPath $SetupExe -Algorithm SHA256).Hash
    Write-Step 'Done'
    Write-Host ("Installer : {0}" -f $setup.FullName)
    Write-Host ("Version   : {0}" -f $Version)
    Write-Host ("Shell     : {0}" -f $ShellName)
    Write-Host ("Size      : {0:N0} bytes ({1:N1} MB)" -f $setup.Length, ($setup.Length / 1MB))
    Write-Host ("SHA-256   : {0}" -f $hash)
    Write-Host ("Elapsed   : {0:N0} s" -f $stopwatch.Elapsed.TotalSeconds)
}
catch [BuildFailure] {
    Write-Host ''
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
catch {
    Write-Host ''
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    $exitCode = 1
}
exit $exitCode
