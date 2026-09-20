<#
.SYNOPSIS
    Installs note-vault: builds or locates the executable, installs it, seeds config, registers
    auto-start, and launches it.

.DESCRIPTION
    Re-running this script is the upgrade path. Run from the repo, it publishes a fresh build
    first; then it stops any running instance, replaces the executable, and restarts - never
    touching your config.yaml or the vault contents.

    Program files go to %LOCALAPPDATA%\Programs\note-vault (replaced on every upgrade). App state
    - the vault.path pointer - stays in %LOCALAPPDATA%\note-vault and survives upgrades.

    A first install creates <vault>\config.yaml from config.default.yaml, which sits next to
    this script.

.PARAMETER Repo
    One or more repository paths to watch. Only used when config.yaml does not exist yet.

.PARAMETER ScanRoot
    One or more folders to auto-discover repositories under. Only used when config.yaml does
    not exist yet; afterwards, edit scan.roots in config.yaml.

.PARAMETER Vault
    Where the vault git repository lives. Defaults to the vault a previous install recorded,
    then C:\NoteVault.

.PARAMETER DefenderExclusions
    Also add the vault and executable to Windows Defender exclusions. Requires an elevated
    session; if you are not elevated the script prints the command to run later.

.PARAMETER Source
    Install this pre-built note-vault.exe instead. Without it the script builds from source when
    it sits in the repo, and otherwise looks for note-vault.exe next to itself.

.EXAMPLE
    .\install.ps1 -Repo D:\Work\example-service,D:\Src\Personal\got-your-back

.EXAMPLE
    .\install.ps1 -ScanRoot D:\Src,D:\Work

.NOTES
    Multiple repos use comma-separated array syntax, not a repeated -Repo switch.
#>
[CmdletBinding()]
param(
    [string[]] $Repo = @(),
    [string[]] $ScanRoot = @(),
    [string]   $Vault,
    [switch]   $DefenderExclusions,
    [string]   $Source
)

$ErrorActionPreference = 'Stop'

# Invoked via `powershell -File`, an array argument arrives as one comma-joined string.
# Normalise both forms so -Repo a,b,c works however the script was launched.
$Repo = @(
    $Repo |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim().Trim('"') } |
        Where-Object { $_ }
)
$ScanRoot = @(
    $ScanRoot |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim().Trim('"') } |
        Where-Object { $_ }
)

$AppName     = 'note-vault'
$ExeName     = 'note-vault.exe'
$ProgramDir  = Join-Path $env:LOCALAPPDATA "Programs\$AppName"   # owned by the installer
$DataDir     = Join-Path $env:LOCALAPPDATA $AppName              # owned by the app
$PointerPath = Join-Path $DataDir 'vault.path'
$StartupDir  = [Environment]::GetFolderPath('Startup')
$ShortcutPath= Join-Path $StartupDir "$AppName.lnk"
$ShutdownEvt = 'Local\note-vault-shutdown'
$ProjectPath = Join-Path $PSScriptRoot "..\src\NoteVault\NoteVault.csproj"
$TemplatePath= Join-Path $PSScriptRoot 'config.default.yaml'

# An upgrade re-run without -Vault must keep pointing at the vault already in use.
if (-not $Vault -and (Test-Path $PointerPath)) {
    $Vault = (Get-Content $PointerPath -Raw).Trim()
}
if (-not $Vault) { $Vault = 'C:\NoteVault' }
$configPath  = Join-Path $Vault 'config.yaml'

function Write-Step($text) { Write-Host "  $text" }
function Write-Fail($text) { Write-Host ""; Write-Host "  X $text" -ForegroundColor Red; Write-Host "" }

# Single-quoted YAML: backslashes stay literal, so Windows paths need no escaping, and
# spaces or a '#' in a path cannot be misread. The only escape is '' for a quote.
function ConvertTo-YamlString([string] $s) { "'" + ($s -replace "'", "''") + "'" }

# Replaces the first match of a line-anchored pattern in the template. Plain string
# splicing, so a '$' in a path is never read as a regex substitution.
function Set-TemplateLine([string] $text, [string] $pattern, [string] $replacement) {
    $m = [regex]::Match($text, $pattern, 'Multiline')
    if (-not $m.Success) {
        throw "config.default.yaml has no line matching '$pattern' - was the template edited?"
    }
    $text.Substring(0, $m.Index) + $replacement + $text.Substring($m.Index + $m.Length)
}

Write-Host ""
Write-Host "note-vault install" -ForegroundColor Cyan
Write-Host ""

# --- 1. git is a hard prerequisite -------------------------------------------------
$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    Write-Fail "git was not found on PATH. note-vault stores everything in a git repository, so nothing works without it."
    exit 1
}
Write-Step "git      $((git --version) -replace '^git version ','')"

# --- 2. build or locate the executable ---------------------------------------------
# Built before the running instance is stopped, so a failed build changes nothing.
if (-not $Source -and (Test-Path $ProjectPath)) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Fail "dotnet was not found on PATH. It is needed to build note-vault from source."
        Write-Host "  Install the .NET SDK, or pass -Source <path to $ExeName> to install a pre-built one."
        Write-Host ""
        exit 1
    }

    $publishDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\publish'))
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    Write-Step "building..."
    & dotnet publish $ProjectPath -c Release -o $publishDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Build failed (see above). Nothing was installed or stopped."
        exit 1
    }
    $Source = Join-Path $publishDir $ExeName
    Write-Step "built    $Source"
}
if (-not $Source) { $Source = Join-Path $PSScriptRoot $ExeName }
if (-not (Test-Path $Source)) {
    Write-Fail "Could not find $ExeName. Expected it at: $Source"
    Write-Host "  Pass -Source <path to $ExeName> if it lives elsewhere."
    Write-Host ""
    exit 1
}

# Checked before anything is stopped: a first install cannot seed config.yaml without it.
if (-not (Test-Path $configPath) -and -not (Test-Path $TemplatePath)) {
    Write-Fail "Could not find config.default.yaml. Expected it next to this script: $TemplatePath"
    exit 1
}

# --- 3. stop any running instance gracefully ---------------------------------------
$running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue
if ($running) {
    Write-Step "stopping running instance..."
    try {
        $evt = [System.Threading.EventWaitHandle]::OpenExisting($ShutdownEvt)
        [void]$evt.Set()
        $evt.Dispose()
    } catch {
        # Not signalable - fall through to the force path below.
    }

    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue
        if (-not $running) { break }
    }
    if ($running) {
        Write-Step "did not exit in 10s - forcing"
        try { $running | Stop-Process -Force -ErrorAction Stop } catch {}
        Start-Sleep -Milliseconds 500
    }
}

# --- 4. copy the executable to a stable location -----------------------------------
# Running from Downloads is how installs quietly break three months later.
New-Item -ItemType Directory -Force -Path $ProgramDir | Out-Null
$targetExe = Join-Path $ProgramDir $ExeName
Copy-Item -Path $Source -Destination $targetExe -Force
Write-Step "exe      $targetExe"

# The app reads this to find its vault before it can read the vault's own config.
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Set-Content -Path $PointerPath -Value $Vault -Encoding utf8 -NoNewline

# Older installs kept the exe in the data folder. The shortcut is re-pointed below.
$legacyExe = Join-Path $DataDir $ExeName
if (Test-Path $legacyExe) {
    Remove-Item $legacyExe -Force
    Write-Step "removed  $legacyExe (old location)"
}

# --- 5. seed config.yaml only if absent --------------------------------------------
New-Item -ItemType Directory -Force -Path $Vault | Out-Null

if (Test-Path $configPath) {
    Write-Step "config   $configPath (existing, left untouched)"
    if ($Repo.Count -gt 0 -or $ScanRoot.Count -gt 0) {
        Write-Host "           note: -Repo/-ScanRoot ignored because config.yaml already exists" -ForegroundColor DarkYellow
    }
} else {
    $yaml = [IO.File]::ReadAllText($TemplatePath)
    $nl = if ($yaml.Contains("`r`n")) { "`r`n" } else { "`n" }

    $yaml = Set-TemplateLine $yaml "^store:[ \t]*'[^'\r\n]*'" ("store: " + (ConvertTo-YamlString $Vault))

    $repoLines = @()
    foreach ($r in $Repo) {
        $full = $r.TrimEnd('\', '/')
        $alias = Split-Path $full -Leaf

        # Catch a bad path here rather than letting it surface as a red icon later.
        if (-not (Test-Path $full)) {
            Write-Host "           ! $full does not exist - skipped" -ForegroundColor DarkYellow
            continue
        }
        if (-not (Test-Path (Join-Path $full '.git'))) {
            Write-Host "           ! $full is not a git repository - skipped" -ForegroundColor DarkYellow
            continue
        }

        $repoLines += "  - path: " + (ConvertTo-YamlString $full)
        $repoLines += "    alias: " + (ConvertTo-YamlString $alias)
    }
    if ($repoLines.Count -gt 0) {
        $yaml = Set-TemplateLine $yaml '^repos:[ \t]*\[\]' ((@('repos:') + $repoLines) -join $nl)
    }

    $validRoots = @()
    foreach ($s in $ScanRoot) {
        $full = $s.TrimEnd('\', '/')
        if (Test-Path $full) { $validRoots += ConvertTo-YamlString $full }
        else { Write-Host "           ! scan root $full does not exist - skipped" -ForegroundColor DarkYellow }
    }
    if ($validRoots.Count -gt 0) {
        # Consumes the trailing "e.g." comment too, which would misread next to real values.
        $yaml = Set-TemplateLine $yaml '^  roots:[ \t]*\[\][^\r\n]*' ('  roots: [' + ($validRoots -join ', ') + ']')
    }

    # No BOM: the file is meant to be hand-edited, and some editors show one as junk.
    [IO.File]::WriteAllText($configPath, $yaml, (New-Object Text.UTF8Encoding $false))
    Write-Step "config   $configPath (created from $(Split-Path $TemplatePath -Leaf))"
}

# --- 6. auto-start: a plain shortcut in the Startup folder --------------------------
# No admin, no service, no scheduled task. Visible in Task Manager > Startup, and the
# app never re-creates it, so disabling it there keeps working.
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($ShortcutPath)
$sc.TargetPath       = $targetExe
$sc.WorkingDirectory = $ProgramDir
$sc.Description      = 'note-vault - automatic history for .notes folders'
$sc.Save()
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
Write-Step "startup  $ShortcutPath"

# --- 7. Defender exclusions (optional, needs elevation) -----------------------------
if ($DefenderExclusions) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($isAdmin) {
        try {
            Add-MpPreference -ExclusionPath $Vault -ErrorAction Stop
            Add-MpPreference -ExclusionPath $targetExe -ErrorAction Stop
            try { Remove-MpPreference -ExclusionPath $legacyExe -ErrorAction Stop } catch {}
            Write-Step "defender exclusions added"
        } catch {
            Write-Host "           could not add exclusions: $($_.Exception.Message)" -ForegroundColor DarkYellow
        }
    } else {
        Write-Host ""
        Write-Host "  Not elevated - skipping Defender exclusions. To add them later, run from an" -ForegroundColor DarkYellow
        Write-Host "  elevated PowerShell:" -ForegroundColor DarkYellow
        Write-Host "    Add-MpPreference -ExclusionPath '$Vault'; Add-MpPreference -ExclusionPath '$targetExe'"
    }
}

# --- 8. launch ----------------------------------------------------------------------
Start-Process -FilePath $targetExe -WorkingDirectory $ProgramDir | Out-Null
Start-Sleep -Milliseconds 1200

$proc = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue
Write-Host ""
if ($proc) {
    Write-Host "  note-vault is running - look for the notebook icon in your tray." -ForegroundColor Green
} else {
    Write-Host "  note-vault did not stay running. Check the log:" -ForegroundColor Red
    Write-Host "    $(Join-Path $Vault 'logs')"
}

Write-Host ""
Write-Host "  exe     $targetExe"
Write-Host "  vault   $Vault"
Write-Host "  config  $configPath"
Write-Host ""
