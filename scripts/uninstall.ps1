<#
.SYNOPSIS
    Removes note-vault. Leaves the vault alone unless you explicitly ask otherwise.

.PARAMETER PurgeVault
    Also delete the vault repository. Prompts first, showing commit count and size.
    Without this the vault is kept - it is the only copy of your notes history.

.PARAMETER RemoveGitignoreEntry
    Also remove the ".notes/" line from your global gitignore. Off by default: it is
    harmless, and other tooling may now rely on it.

.PARAMETER Vault
    Vault path. Defaults to whatever the install recorded, then C:\NoteVault.

.EXAMPLE
    .\uninstall.ps1
#>
[CmdletBinding()]
param(
    [switch] $PurgeVault,
    [switch] $RemoveGitignoreEntry,
    [string] $Vault
)

$ErrorActionPreference = 'Stop'

$AppName      = 'note-vault'
$ExeName      = 'note-vault.exe'
$ProcName     = [IO.Path]::GetFileNameWithoutExtension($ExeName)
$ProgramDir   = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$DataDir      = Join-Path $env:LOCALAPPDATA $AppName
$StartupDir   = [Environment]::GetFolderPath('Startup')
$ShortcutPath = Join-Path $StartupDir "$AppName.lnk"
$ShutdownEvt  = 'Local\note-vault-shutdown'

function Write-Step($text) { Write-Host "  $text" }

Write-Host ""
Write-Host "note-vault uninstall" -ForegroundColor Cyan
Write-Host ""

# Resolve the vault from the pointer the installer wrote, unless told otherwise.
if (-not $Vault) {
    $pointer = Join-Path $DataDir 'vault.path'
    if (Test-Path $pointer) {
        $Vault = (Get-Content $pointer -Raw).Trim()
    }
    if (-not $Vault) { $Vault = 'C:\NoteVault' }
}

$removed = @()
$kept    = @()

# --- 1. stop gracefully -------------------------------------------------------------
# Exit flushes the pending debounce buffer; a bare Stop-Process would discard it.
$running = Get-Process -Name $ProcName -ErrorAction SilentlyContinue
if ($running) {
    Write-Step "stopping note-vault..."
    try {
        $evt = [System.Threading.EventWaitHandle]::OpenExisting($ShutdownEvt)
        [void]$evt.Set()
        $evt.Dispose()
    } catch {}

    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $running = Get-Process -Name $ProcName -ErrorAction SilentlyContinue
        if (-not $running) { break }
    }
    if ($running) {
        Write-Step "did not exit in 10s - forcing"
        try { $running | Stop-Process -Force -ErrorAction Stop } catch {}
        Start-Sleep -Milliseconds 500
    }
    $removed += 'running process stopped'
}

# --- 2. auto-start shortcut ---------------------------------------------------------
if (Test-Path $ShortcutPath) {
    Remove-Item $ShortcutPath -Force
    $removed += "startup shortcut  $ShortcutPath"
}

# --- 3. Defender exclusions ---------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    # The data-folder exe path covers installs from before the program/data split.
    foreach ($p in @($Vault, (Join-Path $ProgramDir $ExeName), (Join-Path $DataDir $ExeName))) {
        try { Remove-MpPreference -ExclusionPath $p -ErrorAction Stop } catch {}
    }
    $removed += 'defender exclusions (if any)'
} elseif ((Test-Path $ProgramDir) -or (Test-Path $DataDir)) {
    $kept += 'defender exclusions - re-run elevated to remove them'
}

# --- 4. program and data directories ------------------------------------------------
if (Test-Path $ProgramDir) {
    Remove-Item $ProgramDir -Recurse -Force
    $removed += "program dir       $ProgramDir"
}
if (Test-Path $DataDir) {
    Remove-Item $DataDir -Recurse -Force
    $removed += "data dir          $DataDir"
}

# --- 5. the vault: never deleted by default -----------------------------------------
if (Test-Path $Vault) {
    if ($PurgeVault) {
        $commits = '?'
        try {
            Push-Location $Vault
            $c = git rev-list --count HEAD 2>$null
            if ($LASTEXITCODE -eq 0) { $commits = $c.Trim() }
            Pop-Location
        } catch { try { Pop-Location } catch {} }

        $bytes = 0
        try {
            $bytes = (Get-ChildItem $Vault -Recurse -File -Force -ErrorAction SilentlyContinue |
                      Measure-Object -Property Length -Sum).Sum
        } catch {}
        $mb = [math]::Round(($bytes / 1MB), 1)

        Write-Host ""
        Write-Host "  About to permanently delete the vault:" -ForegroundColor Red
        Write-Host "    $Vault"
        Write-Host "    $commits commits, $mb MB"
        Write-Host ""
        Write-Host "  This is the only copy of your notes history. It cannot be undone." -ForegroundColor Red
        $answer = Read-Host "  Type DELETE to confirm"

        if ($answer -ceq 'DELETE') {
            Remove-Item $Vault -Recurse -Force
            $removed += "vault             $Vault"
        } else {
            $kept += "vault             $Vault  (purge cancelled)"
        }
    } else {
        $kept += "vault             $Vault"
    }
}

# --- 6. global gitignore entry ------------------------------------------------------
if ($RemoveGitignoreEntry) {
    try {
        $excludes = (git config --global --get core.excludesFile 2>$null)
        if ($excludes) {
            $excludes = $excludes.Trim()
            if ($excludes.StartsWith('~')) {
                $excludes = Join-Path $env:USERPROFILE ($excludes.TrimStart('~').TrimStart('/', '\'))
            }
            if (Test-Path $excludes) {
                $lines = Get-Content $excludes
                $filtered = $lines | Where-Object { $_.Trim() -ne '.notes/' -and $_.Trim() -ne '.notes' }
                Set-Content -Path $excludes -Value $filtered -Encoding utf8
                $removed += "gitignore entry   .notes/ from $excludes"
            }
        }
    } catch {
        $kept += 'global gitignore entry - could not edit it'
    }
} else {
    $kept += 'global gitignore entry (.notes/) - harmless; -RemoveGitignoreEntry to drop it'
}

# --- 7. report ----------------------------------------------------------------------
Write-Host ""
if ($removed.Count -gt 0) {
    Write-Host "  Removed:" -ForegroundColor Green
    foreach ($r in $removed) { Write-Host "    $r" }
}
if ($kept.Count -gt 0) {
    Write-Host ""
    Write-Host "  Kept:" -ForegroundColor Yellow
    foreach ($k in $kept) { Write-Host "    $k" }
}

Write-Host ""
if ((Test-Path $Vault) -and -not $PurgeVault) {
    Write-Host "  Your notes history is still at $Vault - it is a plain git repo."
    Write-Host "  Delete it yourself, or re-run with -PurgeVault."
    Write-Host ""
}
