# note-vault

A background Windows tray daemon that continuously mirrors every `.notes` folder across all your
repositories and worktrees into a single **local, append-only git repository** — one commit per
save cluster. It gives otherwise-unversioned AI-context notes a full, inspectable history with no
per-repo setup.

> `.notes/` is excluded through your **global** gitignore (which note-vault configures on first
> run), so the folder can exist in every repo and worktree while your project's own git never
> touches it.

This repository holds the **source code** of that app. The notes it captures live somewhere else
entirely — see [Two things called "note-vault"](#two-things-called-note-vault).

---

## Two things called "note-vault"

The name is overloaded; keeping the two apart avoids most of the confusion:

| Term | What it is | Where it lives |
|------|------------|----------------|
| **This repo** (the app) | The C# source for the tray daemon, the installer scripts, and the design docs. | Wherever you cloned it, e.g. `C:\Src\<you>\note-vault`. |
| **The vault** (a.k.a. *the store*) | A **separate** local git repo the app creates and writes to at runtime — an append-only mirror of your `.notes`. | A path you choose; default `C:\NoteVault`. Resolved at runtime (see below) — it is **not** inside this repo. |

So "my note-vault is syncing to OneDrive" almost always means *the vault / store*, not this
source checkout.

---

## Repository layout

| Path | Purpose |
|------|---------|
| `src/NoteVault/` | The app — C#, WinForms, `net10.0-windows`, assembly `note-vault`. |
| `scripts/` | `install.ps1`, `uninstall.ps1`, and `config.default.yaml` (the single shipped config template). |
| `doc/` | Design and operations docs. |
| `.notes/` | This repo's *own* notes folder — captured like any other, and gitignored here. |
| `artifacts/` | Build / publish output (gitignored). |

---

## Build & install

Requires the **.NET 10 SDK** and `git` on `PATH`.

```powershell
# From the repo root. When run from the repo, install.ps1 builds via `dotnet publish`,
# installs the exe, writes the vault pointer, and creates a Startup shortcut.
.\scripts\install.ps1 -Vault C:\NoteVault
```

Useful flags:

| Script | Flag | Effect |
|--------|------|--------|
| `install.ps1` | `-Vault <path>` | Where the vault / store lives (default `C:\NoteVault`). |
| | `-Repo <path>` | Explicitly track a repo (repeatable). |
| | `-ScanRoot <path>` | Auto-discover repos under these roots (repeatable). |
| | `-DefenderExclusions` | Exclude the vault + exe from Defender (needs elevation). |
| | `-Source <exe>` | Install a prebuilt exe instead of building. |
| `uninstall.ps1` | `-PurgeVault` | Also delete the vault contents. |
| | `-RemoveGitignoreEntry` | Undo the global-gitignore `.notes/` entry. |

Change behavior by editing `config.yaml` **in the vault** (not `config.default.yaml`), then
restart. The config file is yours — the app never overwrites an existing one.

---

## Where your data lives (runtime)

Once installed, none of the running system lives in this repo:

| What | Path |
|------|------|
| Program exe | `%LOCALAPPDATA%\Programs\note-vault\note-vault.exe` |
| Vault pointer (app state) | `%LOCALAPPDATA%\note-vault\vault.path` |
| Auto-start shortcut | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\note-vault.lnk` |
| **The vault / store** | Whatever `vault.path` contains (default `C:\NoteVault`) |

The vault location is resolved in this order: a `--vault <path>` argument → the `vault.path`
pointer file → the built-in default `C:\NoteVault`. To see where yours points:

```powershell
Get-Content "$env:LOCALAPPDATA\note-vault\vault.path"
```

Inside the vault: `vault/` holds the mirrored notes (the only content you would ever back up),
alongside `.git`, `logs/`, `config.yaml`, and the `roots.json` / `tracked-files.json` /
`repos.json` state.

---

## Keep the vault out of cloud-sync folders

The vault's `.git` gains objects on **every capture**, so putting the vault inside OneDrive /
Dropbox / Google Drive / iCloud makes the sync client re-upload git churn continuously — and a
synced vault is, in effect, a remote (see Security). Keep the vault on a local, encrypted volume.

If you still want the captured **notes** backed up to the cloud, there is a reliable pattern that
syncs only the notes and none of the git churn — see
**[doc/vault-location.md](doc/vault-location.md)**.

---

## Security

The vault captures everything under a `.notes` folder **verbatim and permanently** — including
`.env` files, tokens, and keys — and it is append-only, so a captured secret stays recoverable
even after you rotate it and even after you delete it at the source. Treat the vault as sensitive:
keep it local and encrypted, and **never add a git remote to it**.

---

## Docs

- **[doc/backup-daemon-plan.md](doc/backup-daemon-plan.md)** — the full design: scope, why git is
  the store, discovery, capture, the append-only invariant, install, failure modes, and
  milestones.
- **[doc/vault-location.md](doc/vault-location.md)** — where the vault lives, how to move it, and
  how to keep it out of cloud-sync folders while still backing up your notes.
