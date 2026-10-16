# Vault location & cloud sync

Operational notes for *where the vault lives* and *how to move it*. For what the vault is and why
it is a git repo, see [backup-daemon-plan.md](backup-daemon-plan.md).

## How the location is resolved

At startup the app picks the vault path from the first of these that is set:

1. A `--vault <path>` command-line argument.
2. The pointer file `%LOCALAPPDATA%\note-vault\vault.path` (its trimmed contents).
3. The built-in default `C:\NoteVault`.

`install.ps1 -Vault <path>` simply writes that path into the pointer file. The `store:` line
inside the vault's own `config.yaml` is **informational only** — it does not decide the location.

## Runtime file map

| What | Path |
|------|------|
| Program exe | `%LOCALAPPDATA%\Programs\note-vault\note-vault.exe` |
| Vault pointer | `%LOCALAPPDATA%\note-vault\vault.path` |
| Auto-start shortcut | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\note-vault.lnk` |
| Vault root (`<vault>`) | from the pointer; default `C:\NoteVault` |
| &nbsp;&nbsp;`<vault>\.git` | the append-only history — **changes on every capture** |
| &nbsp;&nbsp;`<vault>\vault\` | the mirrored notes — the only thing worth backing up |
| &nbsp;&nbsp;`<vault>\logs\` | daemon logs (gitignored) |
| &nbsp;&nbsp;`<vault>\config.yaml`, `*.json` | settings and state |

## Moving the vault to a new location

1. Stop the daemon (tray → Quit, or stop the `note-vault` process).
2. Move the whole `<vault>` folder to the new location.
3. Point `vault.path` at it:
   ```powershell
   Set-Content "$env:LOCALAPPDATA\note-vault\vault.path" 'D:\NewPlace\NoteVault' -NoNewline
   ```
4. Start the daemon again and confirm the newest `logs\note-vault-*.log` says
   `note-vault started; vault at <new path>`.

Because the location lives in the pointer, nothing inside the vault needs editing.

## Keeping the vault out of a cloud-sync folder

**Why it matters.** The vault's `.git` gains objects on every capture. Inside OneDrive / Dropbox /
Google Drive / iCloud, the sync client re-uploads that churn continuously. Worse, a synced vault
leaves the machine — and the vault holds unfiltered secrets (see the security section of the
design doc), so a synced vault is a remote with extra steps. **Keep `<vault>` on a local,
encrypted volume.**

**But I still want my notes backed up to the cloud.** You can have that *without* syncing `.git`,
by syncing only the notes. The reliable direction (verified against OneDrive's behavior below) is:
keep the notes **physically inside** the cloud folder, keep the vault **outside** it, and link the
vault's `vault/` subfolder to the notes with a **directory junction**.

Layout (paths are examples — substitute your own):

```
C:\NoteVault\                          <- vault root, OUTSIDE the cloud folder (never synced)
  .git\   logs\   *.yaml / *.json          stay local; the churn never reaches the cloud
  vault\  ──junction──▶  %OneDrive%\Notes\note-vault\vault\   <- notes live here, synced normally
```

Steps:

1. Stop the daemon.
2. Choose a local vault root outside the cloud folder, e.g. `C:\NoteVault`.
3. Make sure the captured notes (`<vault>\vault\`) are **physically** inside the cloud folder,
   e.g. `%OneDrive%\Notes\note-vault\vault\`. Keep `.git`, `logs`, and the config / state files in
   the local vault root — **do not** put them in the cloud folder.
4. Replace the vault's `vault\` with a junction to the cloud location:
   ```powershell
   cmd /c mklink /J "C:\NoteVault\vault" "$env:OneDrive\Notes\note-vault\vault"
   ```
   A **junction** (`mklink /J`) is required, not a symbolic link — see the table below. Junctions
   need no administrator rights, and git treats them as ordinary folders.
5. Point `vault.path` at the local root (`C:\NoteVault`) and start the daemon.
6. Verify: `git -C C:\NoteVault status` is clean and
   `git -C C:\NoteVault rev-list --count HEAD` matches what it was before. The cloud folder should
   now show **only** the `vault\` notes.

### What OneDrive actually syncs

| Link type | Created with | OneDrive behavior |
|-----------|--------------|-------------------|
| **Directory junction** | `mklink /J` | Syncs the *target's contents*. Used here, pointing **from** the local vault **into** the cloud notes folder. |
| Directory symbolic link | `mklink /D` | Ignored — the target's contents are **not** synced. |
| Shortcut (`.lnk`) | Explorer | Only the shortcut file itself syncs. |

Point the junction **from outside the cloud folder into it** (as above) so the sync client only
ever sees real files it owns, not a reparse point it must follow. Pointing a junction the other
way — a link *inside* the cloud folder aimed outward — is the unreliable configuration and is best
avoided.

### Caveats

- **Do not run `git clean -fdx` (or `git checkout -- vault`) in the vault.** Those are the only
  commands that could disturb the `vault` junction. The daemon never runs them.
- With Files On-Demand, cloud-tier (online-only) notes are re-downloaded the first time the daemon
  reads them to commit. This is expected and logged once per path.

## Reverting

Restore the previous `vault.path` (keep a `vault.path.bak` copy before you change it), move the
vault folder back if you moved it, remove any junction with `cmd /c rmdir "C:\NoteVault\vault"`
(this removes the link only — never the target's files), and restart the daemon.
