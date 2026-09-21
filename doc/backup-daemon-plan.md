# note-vault — a git-backed history daemon for `.notes` folders

A background tray application that continuously mirrors every `.notes` folder across all repos
and worktrees into a single **local git repository**, one commit per save cluster.

`.notes/` is excluded via the user's global gitignore — which note-vault sets up itself on first
run (Section 5.0) — so it exists in every repo and every worktree with no per-repo setup, and git
never touches it. It holds context fed to AI chat sessions and the output that came back. That
content is valuable, unversioned, and currently protected by nothing.

**The vault is append-only.** Files are added and updated; nothing is ever removed from it.
See Section 5.5 — this is the design's central invariant and it simplifies a great deal.

---

## 1. Scope — and why it changes the design completely

An earlier draft of this plan watched whole source roots (`D:\Src\corp`, ...). That drove
everything: three-stage ignore filters, 64 KB watcher buffers, overflow recovery, a
content-addressed blob store, tiered retention, mark-and-sweep GC, and a hard "never walk a
root" constraint, because walking `D:\Src\corp` is genuinely expensive.

**Scoping to `.notes` deletes most of that.** A `.notes` folder is a few dozen small text files.
Concretely, on this machine today: `D:\Work\example-service\.notes` is one file, 8 KB.

**The no-walk constraint is deliberately dropped, and this is a reversal worth being explicit
about.** It was never about walks being distasteful; it was about not paying a cost proportional
to repo size. A full enumeration of a `.notes` folder is a few dozen `stat` calls — under a
millisecond. There is nothing left to protect against, and holding the constraint anyway would
cost real coverage: it forced a one-version-per-file blind spot on first touch, and left a
permanent hole for files changed while the app was down. Both close for free now.

So: **walk freely, but only ever inside a `.notes` folder.** Nothing in this design enumerates a
repo, a worktree, or `D:\Src`. Discovery goes through git (Section 5.1), never through a scan.

| | Old scope (whole roots) | This scope (`.notes` only) |
|---|---|---|
| Files under watch | 10^5 – 10^6 | 10^1 – 10^3 |
| Ignore rules | 3-stage filter, essential | one temp-file skip list (Section 6) |
| Buffer overflow | routine (`npm install`) | effectively impossible |
| Full enumeration | forbidden | free, ~1 ms |
| Storage engine | custom CAS + SQLite | **git** |
| Retention / GC | load-bearing | `git gc`, weekly |
| Deletion handling | tombstones, restore semantics | **none — append-only** |

---

## 2. Why git is the store

Small, text, append-mostly, per-worktree. That is precisely what git is built for, and the
earlier plan's reason for rejecting git-based tools — "roots are arbitrary folders, not
repositories" — evaporates once the root is a folder you can simply *make* into a repo.

What you get for free, none of which has to be written or tested:

| Need | Custom CAS design | Git |
|---|---|---|
| Content-addressed dedup | build it | built in |
| Compression | zstd integration | zlib + packfile **deltas** |
| History query | SQLite schema + indexes | `git log --follow` |
| Diff | build it, incl. binary detection | `git diff` |
| Restore | build it | `git show <rev>:<path>` |
| Integrity check | custom verify pass | `git fsck` |
| Housekeeping | tiered retention + mark-and-sweep GC | `git gc` |

Packfile delta compression matters more here than generic dedup would. AI conversation logs grow
by **appending** — each save is the previous file plus a bit more. Whole-file content addressing
stores a complete new blob every time; git deltas the near-identical blobs against each other.
This is the access pattern git is best at and whole-file CAS is worst at.

What you write is only the part that does not exist: setup, discovery, watching, debouncing,
mirroring, committing, and the tray UI.

**Technology:** .NET 10, single-file self-contained **WinExe** (no console window).
`FileSystemWatcher` for watching, `System.Threading.Channels` for the queue, WinForms
`NotifyIcon` for the tray, and **shelling out to `git.exe`** rather than LibGit2Sharp — the
operations are a handful of plumbing commands, and matching stock git's behaviour exactly is
worth more than avoiding a process spawn at this volume.

---

## 3. Architecture

```
   First run: ensure .notes/ is in the global gitignore  (5.0)
            |
            v
   git worktree list  (per configured repo, on startup + 60 s poll)
            |
            v
   +---------------------------+
   | Discovery                 |   repo -> [worktree...] -> <wt>\.notes
   |  never scans the disk     |   attaches a watcher per existing .notes
   +-------------+-------------+   full walk of each NEW .notes (cheap)
                 |
                 v
   +---------------------------+
   | Watcher (1 per .notes)    |   IncludeSubdirectories = true
   |  callback: enqueue only   |   Deleted events discarded outright
   +-------------+-------------+
                 | Channel<RawEvent>
                 v
   +---------------------------+
   | Skip list (filename only) |   ~$*, *.tmp, *.partial, ...  (Section 6)
   |  the ONLY filter          |   configurable; nothing else is excluded
   +-------------+-------------+
                 v
   +---------------------------+
   | Coalescer (3 s debounce)  |   long window on purpose: AI tools stream
   |  per .notes-root batching |   output, and we want coherent snapshots
   +-------------+-------------+
                 v
   +---------------------------+
   | Mirror + Commit           |   copy into the vault tree, add, commit.
   |  SINGLE WRITER, ADD-ONLY  |   never deletes, never stages a removal
   +-------------+-------------+
                 v
   +------------------------------------------------+
   |  C:\NoteVault\   (a local git repo)            |
   |    vault\<alias>\<worktree>\...                |
   +------------------------------------------------+
                 ^                        |
                 |                        v
   +-------------+-------------+   +--------------------------+
   | Reconcile                 |   | Tray: Status / Open      |
   |  startup / resume / error |   |       vault / Quit  (§8) |
   +---------------------------+   +--------------------------+

   Retrieval is not in this diagram on purpose: the vault is an ordinary git
   repo, so history, diff and restore are whatever git tooling you already use.
```

---

## 4. Vault layout

```
C:\NoteVault\
  .git\
  .gitattributes                     * -text   (byte-exact round-trip)
  README.md                          written on first run — see below
  config.yaml
  roots.json                         alias -> source path, active/retired, first seen
  vault\
    work\
      example-service\               <- the primary worktree
        .notes\env-dev\.env
      InDevelop-TICKET-1042-example-bug-fix\
        .notes\...
        src\api\.env                 <- individually tracked (5.8), same coordinate system
    gyb\
      got-your-back\
  logs\note-vault-20260910.log       NOT tracked by the vault repo
```

Worktree naming follows the convention already in use — `<repo>.worktrees/<flattened-branch>` —
so the vault subdirectory is just the worktree directory's basename. That is unique within a repo
by construction, since worktrees are directories. `roots.json` records the full mapping so a
collision is detectable rather than silent, and so the vault is self-describing if you ever open
it without the app.

**`vault/` holds nothing but real captured content — no per-folder marker file.** A worktree's
identity (source path, repo, alias, first seen) lives in `roots.json` at the vault root instead.
The consequence, accepted rather than worked around: git cannot track an empty directory, so a
worktree with nothing captured yet simply has **no folder under `vault/` at all** until real
content lands — there is no synthetic placeholder to give it an early, empty presence. An earlier
revision wrote a `.note-vault-root(.json)` file into each worktree folder specifically to force
that empty-directory presence; removed once it became clear that duplicated `roots.json` and put
non-worktree content inside `vault/`, which is exactly what `vault/` should never contain.

Because the vault is append-only, **its working tree is the union of every file ever seen** —
not a mirror of what currently exists on disk. Browsing `C:\NoteVault\vault\...` in Explorer shows
notes from deleted files and retired worktrees, at their last known content, with no git commands
needed. That is a deliberate property, not an accident.

### 4.1 `README.md` — the vault must explain itself

Since note-vault deliberately ships no browse or restore UI (Section 8), **the vault has to be
self-documenting**, or its two non-obvious conventions become traps for whoever opens it in a git
client six months from now. note-vault writes and commits a `README.md` at the vault root on first
run covering:

- **Append-only.** A file that looks present may have been deleted from the source long ago, and
  no commit will ever show its deletion. Absence of a deletion is not evidence of existence.
- **`_nv_git_` path segments** are a mangled `.git` (Section 5.4) and must be renamed back when
  restoring by hand — the reason being that git would otherwise store a gitlink and drop the
  contents entirely.
- **Do not change `core.autocrlf` or the `* -text` attribute.** Both exist so a hand-run
  `git show`/`git checkout` returns bytes identical to what was captured; changing them silently
  corrupts restores of files with mixed or non-native line endings.
- The two commands worth knowing: `git log --follow -- vault/<alias>/<wt>/<path>` and
  `git show <rev>:<path> > out`.
- **This repo contains unfiltered secrets. Do not add a remote.** (Section 6, Section 11.)

---

## 5. Component detail

### 5.0 First-run setup

Two things, both idempotent and re-checked on every start.

**Vault initialisation.** `git init` the store if absent, then set the vault-local config that
Section 5.4 depends on (`core.autocrlf=false`, `core.safecrlf=false`, `core.fileMode=false`,
`core.longpaths=true`, `user.name`, `user.email`), write `.gitattributes` with `* -text`, and
write and commit `README.md` (Section 4.1). The README matters more than it looks: it is the only
thing that explains the vault's conventions to a future reader, because there is no UI that does.

**Global gitignore.** Ensure every configured notes folder name is globally ignored:

1. `git config --global --get core.excludesFile`.
2. If unset, set it to `~/.gitignore_global`. **Never overwrite an existing value** — if the user
   already points it somewhere, that file is the one to edit.
3. Create the file if missing.
4. For each name in `notesDirNames` (or the single `notesDirName`, Section 9), if no line already
   equals `<name>/`, append it.
5. Log exactly what changed, and show a tray notification the first time it modifies anything.

This is the one thing note-vault writes outside its own directory, so it is deliberately narrow:
one appended line per name, no rewriting, no reordering, no removal. Controlled by
`setup.ensureGlobalGitignore` (default `true`) for anyone who wants to manage it by hand.

On this machine it is already a no-op — `~/.gitignore_global` already contains `.notes/`.

**Caveat worth knowing:** a global gitignore does not untrack files already committed to a repo.
If some repo has `.notes` checked in, it stays tracked there, and note-vault will still mirror it.

### 5.1 Discovery — ask git, never scan

For each repo in config, run `git worktree list --porcelain`. That yields every worktree path
exactly, including ones created since the app started. `.notes` is at `<worktree>\.notes`.

Runs on startup and every 60 s. A 60 s latency for a brand-new worktree is irrelevant, because
the first thing done to a newly discovered `.notes` is a **full walk and capture** of it — so
nothing created in that window is missed.

Consulting git here does **not** reintroduce the footgun the earlier plan warned about. That
warning was specifically about `respectGitignore` as a *capture filter*, which would have
silently disabled the whole purpose. Using git for *discovery* serves that purpose instead: it is
how you find the gitignored folders in the first place.

Config also accepts `extraNotesDirs` for a `.notes` that lives outside any repo.

**A `.notes` that does not exist yet** cannot be watched — `FileSystemWatcher` requires the
directory to exist. It is simply picked up by the next discovery poll, walked, and committed.

### 5.2 Watcher

One `FileSystemWatcher` per discovered `.notes`, `IncludeSubdirectories = true`,
`NotifyFilter = FileName | DirectoryName | LastWrite | Size`. The callback does nothing but
enqueue into an unbounded `Channel` and return.

`InternalBufferSize = 64 KB` is kept even though overflow is now near-impossible — `.notes` is
gitignored, so `git checkout`, rebase, and stash never touch it, and no build tool writes there.
The `Error` handler still exists and now has a trivially correct response: walk that one `.notes`
folder and commit whatever is new or changed.

**`Deleted` events are discarded on arrival.** Nothing downstream needs them. This single line
erases an entire category of bug that the previous designs had to work around — Office and many
editors save by writing a temp file, *deleting the original*, and renaming the temp over it, so
any design that acts on delete events records a spurious deletion on every save. Append-only
means there is nothing to get wrong.

`Renamed` is treated as "content changed at the destination path". The source path is left
untouched in the vault (Section 5.5).

### 5.3 Coalescer

A per-path debounce, batched per `.notes` root, flushing when the root has been quiet for
`debounceMs` (**default 3000**).

This is a deliberate reversal of the earlier plan's "keep the window short" guidance. That
advice optimised for not losing work if the process dies mid-window. Here the dominant concern
is different: AI tooling *streams* output into `.notes`, so a 750 ms window would commit
half-written responses and produce a history full of fragments. A 3 s window yields coherent
snapshots, and the cost of the longer window is bounded by the reconcile walk on next start.

Flush every pending root synchronously on application exit.

### 5.4 Mirror and commit

Per flush, for one `.notes` root, on a **single writer thread** (git's `index.lock` permits
exactly one writer, and contention here is a corruption-shaped bug):

```
for each path in batch:                       # deletions never reach here
    src = <worktree>\<rel>                    # rel includes the notesDirName segment for a notes file
    dst = C:\NoteVault\vault\<alias>\<wt>\<mangled rel>
    copy src -> dst (creating dirs)

git -C C:\NoteVault add -f --ignore-removal -- <the paths just written>
git -C C:\NoteVault diff --cached --quiet && exit          # nothing actually changed
git -C C:\NoteVault commit -m "<message>"
```

Five details that are each a bug if missed:

- **`--ignore-removal`, and never `-A`.** This is what makes append-only actually hold at the git
  level. Since git 2.0, a bare `git add <pathspec>` *does* stage removals for files missing from
  the working tree — so if anything ever deletes from the vault tree (a stray cleanup, AV
  quarantine, a synced folder), a plain `git add` would quietly commit that deletion.
  `--ignore-removal` makes the add path structurally incapable of recording one.
- **Stage explicit paths, not the root.** Pass the files just written, not
  `-- vault/<alias>/<wt>`. Same reasoning: narrow the blast radius to what this flush touched.
- **`git add -f`.** A `.gitignore` *inside* someone's `.notes` folder would otherwise silently
  suppress capture of exactly the files this app exists to protect.
- **A nested `.git` becomes a gitlink and its contents are lost.** If a `.notes` folder ever
  contains a git repo, git records a submodule pointer instead of the files. Mangle any path
  segment equal to `.git` to `_nv_git_` on the way in, and reverse it on restore.
- **`git diff --cached --quiet` before committing** kills the "saved with no changes" problem for
  free — the same job the old design needed a hash comparison for.

Plus **byte-exact round-trip**, which is configuration rather than code: `core.autocrlf=false`,
`core.safecrlf=false`, `core.fileMode=false`, `core.longpaths=true`, and a vault-root
`.gitattributes` of `* -text`. Without this, git rewrites line endings and a restored file
differs from what was saved.

Commit message:

```
work/InDevelop-TICKET-2077-example-feature: 2 files

+ chat/2026-09-10-token-refresh.md
~ context/schema.sql

captured: 2026-09-10T14:32:11Z
source: watch
```

Only `+` (new) and `~` (updated) appear — there is no deletion marker, by construction. `source`
is `watch`, `reconcile`, or `discover`. Commit author comes from vault-local `user.name` /
`user.email` so the vault does not depend on global git config.

Git does not store mtime. The commit timestamp records *when the app saw the change*, which is
the useful figure; the file's own mtime is not preserved. Stating that plainly rather than
building a sidecar manifest for it.

### 5.5 Append-only — the central invariant

**Nothing is ever removed from the vault.** No `git rm`, no deletion of mirrored files, no
tombstones, no "does the path still exist" check when a window closes. If a note existed once,
it stays in the vault at its last known content, forever.

What this buys:

- The spurious-deletion problem from atomic saves disappears entirely rather than being mitigated.
- Worktree removal needs no special handling — see 5.6.
- The vault working tree becomes a browsable union of everything (Section 4), so recovering a
  deleted note needs no history query at all.
- Reconcile gets simpler: walk, copy what is new or changed, commit. It never has to reason about
  absence.

What it costs, stated honestly:

- **A rename leaves both names in the vault.** `notes.md` renamed to `design.md` yields two files
  in the vault, the old one frozen at its pre-rename content. `git log --follow` will not connect
  them, because from git's perspective the old file was never deleted.
- **The vault never shrinks.** Deleted notes, abandoned drafts, and files from removed worktrees
  accumulate. At kilobyte-scale text this is irrelevant for years; it is worth knowing rather than
  discovering.
- **The vault does not tell you what currently exists on disk.** It is a record of everything
  seen, not a mirror. If you need "what is in this worktree right now", look at the worktree.

### 5.6 Worktree retirement

`git worktree remove` deletes `.notes` wholesale. Under append-only there is nothing to do about
the files — they simply stay. When discovery reports a worktree that is no longer listed (or
whose directory is gone), mark it `retired` in `roots.json`, stop watching it, and commit that one
metadata change so the timeline records when it happened.

The mirrored notes stay exactly where they are, browsable in Explorer and in the tray's history
window. Retirement is a bookkeeping event, not a data event.

### 5.7 Reconcile

On **startup**, on **resume from sleep**, and on any watcher `Error`: walk each active `.notes` in
full, copy anything new or changed, and commit as `source: reconcile`.

This is the piece the no-walk constraint made impossible and which now costs nothing. It gives
complete coverage of app downtime and closes the first-touch gap entirely — a file's first
captured revision is its *actual* content at discovery, not its content after the first edit that
happened to be observed.

### 5.8 Tracked files — individually, outside `.notes`

A second, smaller capture path for one-off files that matter but do not live in `.notes` — the
motivating case is a `.env` at a fixed repo-relative path (e.g. `src/api/.env`) that recurs under
every worktree of a `repo.worktrees\branch` layout. Rather than pin one absolute path, the user
declares a **relative path** (e.g. `src/api/.env`) and it is checked against **every** live,
non-retired worktree — one entry follows the file across every worktree (or repo) that happens to
have it.

Patterns are user-managed from the tray ("Tracked files…"), not `config.yaml` — that file is
never rewritten by the app after first run. They persist to `<vault>\tracked-files.json`, which is
itself versioned in the vault repo alongside `roots.json`.

Mechanically this rides the same debounce/commit pipeline as `.notes` (Sections 5.2–5.4), and lands
in the vault at its **real, unmodified relative path from the worktree root** — the same
coordinate system a mirrored notes file now uses (5.4 mirrors `<worktree>\<rel>`, not
`<worktree>\.notes\<rel>` stripped of its prefix). Because both are relative to the same root,
there is nothing to reconcile between them and no separate namespace is needed: the vault simply
mirrors the worktree's actual layout, `.notes` folder included, so a tracked file sits exactly
where it does on disk.

The one real difference from `.notes` is watcher scope: one non-recursive watcher per (worktree,
containing folder), filtered to the exact declared filename(s) in the callback —
`FileSystemWatcher.Filter` only accepts one glob, and everything else in that folder is
deliberately ignored.

**First-appearance is a known, accepted gap.** `FileSystemWatcher` requires the target directory to
exist, so a watcher is only attached once a tracked file has already been seen once. A file that
does not exist yet anywhere is only picked up on the next resolve pass — bounded by the cheap tick
(`discovery.tickSeconds`, default 900 s / 15 min), the hourly full discovery, or immediately after
editing the list from the tray (which forces a rescan). Once seen once, further edits are watched
live, same as `.notes`. Treated the same as the other schedule figures in the Status window
(Section 8.1): an upper bound, not a promise — closing it with a dedicated appearance-watcher
(mirroring 5.1's parent-folder watch for a `.notes` that does not exist yet) is possible later if
15 minutes proves too slow in practice.

---

## 6. Filtering — temp files only

There is exactly **one** filter: a configurable list of filename globs for editor and OS scratch
files. It is matched against the **filename only**, never the directory path, so it cannot
accidentally exclude a whole folder.

```
~$*            Office lock files
*.tmp
*.partial
*.crdownload
*.swp  *.swo   vim
*~             emacs / gedit backups
.#*            emacs lock files
Thumbs.db  desktop.ini  .DS_Store
```

Append-only makes this list more valuable than it would otherwise be: a temp file captured once
lives in the vault permanently and cannot be tidied away later, so keeping the junk out at capture
time is the only chance to keep it out at all.

**Nothing else is filtered.** Specifically, and deliberately:

- **No size cap.** A large file pasted into `.notes` is committed in full. Captures over
  `logLargeCaptureBytes` (default 25 MB) are logged so the growth is visible — logging is not
  filtering, and nothing is skipped.
- **No extension denylist**, no binary exclusion, no content inspection.
- **Secrets are captured, and permanently.** The one real `.notes` on this machine today contains
  `env-dev/.env`. Every credential written into a `.notes` folder enters the vault and stays
  recoverable after rotation — git history is immutable, and append-only means even the working
  tree keeps it. This is accepted by decision: the vault is local, and the containment is that it
  stays on this machine (Section 11).
- **Cloud on-demand placeholders will be rehydrated.** If a `.notes` folder sits under OneDrive
  with online-only files, reading them forces a download. Capture proceeds anyway; the read is
  logged once per path.

---

## 7. Install

A tray application, not a service. It runs as your user, in your session. Two scripts ship next
to the executable and are the entire install story — reading this section should never be a
prerequisite for either working:

```
install.ps1     [-Repo <path>]...  [-Vault C:\NoteVault]  [-DefenderExclusions]  [-Source <exe>]
uninstall.ps1   [-PurgeVault]      [-RemoveGitignoreEntry]
```

Program files and app state live apart, the layout a future per-user installer expects — it
may own and wipe the program folder, and the vault pointer must survive that:

```
%LOCALAPPDATA%\Programs\note-vault\note-vault.exe   program files, replaced on every upgrade
%LOCALAPPDATA%\note-vault\vault.path                app state, survives upgrades
```

### 7.1 How auto-start works

A plain shortcut in the user's Startup folder:

```
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\note-vault.lnk
    -> %LOCALAPPDATA%\Programs\note-vault\note-vault.exe
```

No administrator rights, no service, no scheduled task. Chosen over the alternatives for reasons
worth recording:

| Mechanism | Why not |
|---|---|
| `HKCU\...\CurrentVersion\Run` | Works and is one registry write, but a shortcut is a *file* — visible, movable, deletable by hand if the scripts are ever lost |
| Task Scheduler at logon | Buys restart-on-failure, which sounds valuable here and is not: a crash costs only the current 3 s debounce window, because startup reconcile (5.7) re-captures everything at next launch. Not worth the task XML |
| Windows Service | Cannot own a tray icon at all, and under `SYSTEM` cannot see your profile ACLs or mapped drives |

The shortcut also shows up in **Task Manager → Startup**, so Windows' own toggle works on it. That
has one design consequence: the app must never re-create or "repair" the shortcut on launch, which
would silently override a user who disabled it there.

### 7.2 `install.ps1`

1. Verify `git.exe` is on `PATH`; fail immediately with one clear line if not, since nothing works
   without it.
2. Get the executable: `-Source` if given; else, when the script sits in the repo, a fresh
   `dotnet publish` into `artifacts\publish`; else `note-vault.exe` next to the script. The build
   runs before anything is stopped, so a failed build leaves the installed version untouched.
   Copy the exe to `%LOCALAPPDATA%\Programs\note-vault\` and write the vault pointer to
   `%LOCALAPPDATA%\note-vault\vault.path`. Running from `Downloads` is how installs quietly break
   three months later. An exe left in the data folder by an older install is removed.
3. Create `<vault>\config.yaml` if absent, copied from `scripts\config.default.yaml` (which ships
   next to the script) with `store`, `repos` and `scan.roots` filled in from `-Vault`, `-Repo` and
   `-ScanRoot`. **If it already exists, leave it completely untouched.**
4. Create the Startup shortcut.
5. With `-DefenderExclusions` on an elevated session, exclude the vault and the exe (AV file locks
   are the main source of read retries). If requested without elevation, print the one command to
   run later rather than silently skipping or triggering a UAC prompt mid-install.
6. Stop any running instance (7.4), then launch the new one.
7. Print three lines — exe path, vault path, config path — and nothing else.

**Re-running it is the upgrade path**: build, stop, replace the exe, restart, never touching config
or vault contents. Without `-Vault` a re-run keeps the vault the previous install recorded.

### 7.3 `uninstall.ps1`

1. Stop the running instance gracefully (7.4).
2. Remove the Startup shortcut.
3. Remove the Defender exclusions, if elevated.
4. Delete `%LOCALAPPDATA%\Programs\note-vault\` and `%LOCALAPPDATA%\note-vault\`.
5. Leave the `.notes/` line in the global gitignore unless `-RemoveGitignoreEntry` is passed —
   it is harmless, and other tooling may now depend on it.
6. Print what was removed and, more importantly, what was kept and where.

**The vault is never deleted.** `-PurgeVault` is the only way, and it prompts first, showing the
commit count and on-disk size. The vault is the only copy of years of notes, and the whole design
is append-only precisely so nothing is lost by accident — an uninstaller that helpfully tidied it
away would undo the entire point of the tool.

### 7.4 Stopping gracefully

The app holds a named `EventWaitHandle` (`Local\note-vault-shutdown`). Both scripts set it and wait
up to 10 seconds before force-killing. This is not ceremony: exit flushes the pending debounce
buffer (5.3), and a bare `Stop-Process` discards whatever was mid-window.

### 7.5 Build

Publish as a single-file self-contained `WinExe` so there is no runtime prerequisite and no
console window flash at logon. Logs roll under `<vault>\logs\`, which the vault repo does not
track.

---

## 8. Tray application

There is no CLI, no file browser, and no restore UI. **Retrieval is plain git.** The vault is an
ordinary repo, so history, diff, blame, and restore are whatever git tooling you already use, and
building a window on top of that would only re-implement `git log` and `git show` less well.

```
  note-vault
  ─────────────────────
  Status…
  Tracked files…          Add/remove relative paths tracked outside .notes (5.8)
  Open vault folder        Explorer at C:\NoteVault\vault
  ─────────────────────
  Quit
```

**Two icon states, and only two** — normal and error. The whole feedback loop is: the icon turns
red, you open Status, you see which root is broken and why. Nothing else interrupts you.

The "capturing" pulse from an earlier draft is dropped. Capture happens constantly, so a
flickering tray icon is noise rather than information, and the Status window already shows the
last capture time per root.

**Normal means nothing needs reviewing.** Not "nothing is happening" — a healthy system is busy,
and most unremarkable conditions are simply healthy: a repo with no `.notes` folder yet, a retired
worktree, a large file captured, a root untouched for a month. None of those are surfaced
anywhere but the log. There is no intermediate "attention" state, on purpose.

The icon turns red for exactly five conditions, every one of them *note-vault tried to do its job
and could not*:

- A watcher failed to attach, or dropped and could not be re-attached.
- Discovery failed for a configured repo — bad path, or not a git repository.
- A git command failed (add, commit, init).
- A file could not be read after its retries were exhausted.
- The global gitignore could not be updated (Section 5.0).

Red clears when every one of those has cleared on a later successful operation. Nothing outside
that list ever colours the icon — an icon that goes red for things you would not act on is an
icon you stop looking at.

A third state exists implicitly and is worth naming: **no icon at all means the app is not
running** and nothing is being captured. That is what the Startup shortcut (Section 7.1) prevents
and what the startup reconcile (5.7) exists to recover from.

`tray.notifyOnError` can additionally raise a balloon on the first error, but it defaults to
**off** — the red icon is the signal you asked for, and a balloon for a condition that is already
visible is just another thing to dismiss.

Settings that would otherwise be menu toggles — start with Windows, weekly `git gc` — live in
`config.yaml` and are applied at launch. Config and logs sit in the vault folder and open like
any other file.

### 8.1 Status window

Read-only, auto-refreshing every `tray.statusRefreshSeconds` (default 3), and the single place
that answers *"is this actually working?"* Closing it does not quit the app.

```
 ┌ note-vault ────────────────────────────────────────────────── ▲ error ─┐
 │ Vault    C:\NoteVault    1,284 commits   18.4 MB   last gc 3 days ago  │
 │ Queue    0 pending       discovery 41 s ago    reconcile 06:12 today   │
 │                                            [Force refresh]  [Pause]   │
 ├────────────────────────────────────────────────────────────────────────┤
 │ repo / worktree / file             files   last capture       state    │
 │ work / example-service                 1   2026-09-10 14:32   watching │
 │ work / TICKET-1042-example-bug-fix…    7   2026-09-10 09:15   watching │
 │ work / TICKET-2077-example-feature…    3   2 min ago          watching │
 │ tools / work-tools                     0   —                  no .notes│
 │ gyb / got-your-back                   12   yesterday 18:22    watching │
 │ work / TICKET-1900-old-branch          18   2026-08-14 11:03   retired  │
 │ ciam-app / InDevelop…/src/api/.env     1   6 min ago          tracked  │
 │ src/some/other.env (not found)         —   —                  not found│
 ├─ errors ───────────────────────────────────────────────────────────────┤
 │ ✖ tools: git worktree list failed — not a git repository               │
 │ ✖ work/TICKET-2077: chat/transcript.md locked, 3 retries — will retry  │
 └────────────────────────────────────────────────────────────────────────┘
```

Three bands:

**Vault summary.** Store path, commit count, on-disk size, last `git gc`, pending queue depth,
when discovery and reconcile last ran, and two buttons: **Pause** (5.2) and **Force refresh** — a
manual override that re-runs discovery, reconciles every root, and re-resolves every tracked-file
pattern immediately, rather than waiting for the schedule. Queue depth is the one number that
reveals a stuck writer thread.

**One combined table** — notes-folder roots and individually tracked files (5.8) together, not two
separate lists, so one place answers "what is note-vault tracking" completely. A tracked file's
row appends its relative path onto its worktree in the first column; its `files` column is always
`1`. A declared pattern with no current match still gets a row (rather than vanishing silently),
so a typo or a not-yet-created file is visible instead of silent. States:

| State | Meaning |
|---|---|
| `watching` | Watcher attached, `.notes` exists |
| `no .notes` | Worktree known, folder not created yet — informational, not a problem |
| `retired` | Worktree gone; notes preserved in the vault (5.6) |
| `error` | Watcher failed to attach, or captures are failing — details in warnings |
| `tracked` / `not found` | An individually tracked file (5.8): currently matched, or not |

**Errors, per repo/project** — exactly the five conditions from Section 8, and nothing else. Each
carries the root it belongs to, so *which project is broken* is answerable at a glance. They are
sticky until the underlying condition clears on a later successful operation, so a transient
failure at 03:00 is still visible at 09:00.


When there is nothing wrong, this band is empty and the icon is normal. An empty band is the
expected steady state, not a sign the window failed to load.

One small convenience worth having: double-clicking a row opens that root's vault folder in
Explorer. That is a shortcut to the filesystem, not a browse UI.

---

## 9. Configuration

`C:\NoteVault\config.yaml`

```yaml
store: C:\NoteVault            # a local git repo

notesDirName: .notes
# notesDirNames: [.notes, .ai-notes]  # optional; several, independently watched (see below)
debounceMs: 3000              # long on purpose: AI tools stream output

setup:
  ensureGlobalGitignore: true # append "<name>/" to the global gitignore for each configured name (5.0)

repos:
  - path: D:\Work\example-service
    alias: work
  - path: D:\Work\work-tools
    alias: tools
  - path: D:\Src\Personal\got-your-back
    alias: gyb

extraNotesDirs: []            # .notes folders outside any repo

discovery:
  pollSeconds: 60             # git worktree list; never a filesystem scan

reconcile:
  onStartup: true
  onResume: true
  onWatcherError: true

capture:
  appendOnly: true            # not configurable off; recorded here as documentation
  skipTempFiles:              # matched against FILENAME only — the only filter there is
    ["~$*", "*.tmp", "*.partial", "*.crdownload", "*.swp", "*.swo", "*~",
     ".#*", "Thumbs.db", "desktop.ini", ".DS_Store"]
  logLargeCaptureBytes: 26214400   # log-only; never skips

tray:                         # auto-start is install.ps1's job (7.1), not a setting here
  statusRefreshSeconds: 3
  notifyOnError: false        # the red icon is the signal; balloons are opt-in

maintenance:
  gcWeekly: true
```

`trackedFiles` (Section 5.8) is deliberately not a `config.yaml` key — it is user-managed from the
tray and persisted to `<vault>\tracked-files.json`, so the app can rewrite it without touching the
file the user hand-edits.

`notesDirNames` (plural) is the array form of `notesDirName`, added once the vault-layout revision
(5.4/5.8) made it safe: each configured name is auto-discovered and watched **independently** in
every worktree — e.g. both `.notes` and `.ai-notes` at once — and, because a captured file now
keeps its real worktree-relative path (folder name included) rather than being flattened, two
differently-named notes folders can never collide on the same vault path. When `notesDirNames` is
non-empty it wins outright; the singular `notesDirName` stays only for an existing config.yaml
that has never been touched to add the plural key.

Sources are on `D:`, so `C:\NoteVault` survives a `D:` failure. The vault holds text at kilobyte
scale, so sizing is a non-issue for years.

---

## 10. Failure modes

| Failure | Mitigation |
|---|---|
| Two commits race, `index.lock` conflict | Single writer thread; all commits serialized through one channel |
| A stray delete in the vault tree gets committed | `git add -f --ignore-removal` with explicit paths; never `-A` |
| `.notes` contains a nested `.git` → gitlink, contents lost | Mangle `.git` path segments to `_nv_git_`; reverse on restore |
| `.gitignore` inside `.notes` suppresses capture | `git add -f` — force past all ignore rules |
| Line endings rewritten, restore is not byte-exact | `core.autocrlf=false`, `core.safecrlf=false`, vault `.gitattributes` = `* -text` |
| Save recorded as a deletion (write-temp-then-rename) | Cannot happen — `Deleted` events are discarded and the vault is append-only |
| Rename leaves a stale copy under the old name | Accepted cost of append-only (5.5); `--follow` will not link the two |
| Global gitignore already configured elsewhere | Never overwrite `core.excludesFile`; append to whatever it points at, idempotently |
| Repo has `.notes` already committed | Global ignore does not untrack it; it is mirrored anyway. Documented, not worked around |
| New worktree not yet known | 60 s discovery poll, then full walk of its `.notes` — nothing missed in the gap |
| Worktree removed | Marked retired in `roots.json`; notes stay in the vault untouched |
| App not running for hours/days | Startup reconcile walks every `.notes` — cheap and complete. Intermediate states are lost |
| Watcher buffer overflow | Near-impossible for a gitignored notes folder; `Error` handler walks that one folder |
| Secrets captured into permanent history | Accepted by decision (Section 6). Vault is local; enforcement deferred (Section 11) |
| Huge file bloats the vault permanently | Accepted; logged over 25 MB. Never skipped |
| Hand-restoring a `_nv_git_` path without renaming it back | Vault `README.md` documents the mangling — there is no restore UI to reverse it for you (4.1) |
| Someone "fixes" `core.autocrlf` on the vault, corrupting later hand-restores | `README.md` states why `* -text` and `autocrlf=false` are there; setup re-asserts them on every start |
| Path exceeds 260 chars | `core.longpaths=true` on the vault repo |
| Vault repo slows as commits accumulate | Weekly `git gc` from the tray menu or the scheduler |
| File locked by editor or AV at copy time | Retry 3x at 50/200/500 ms, then requeue once; log if still failing |
| Tray app crashes | The missing tray icon is itself the signal — nothing is being captured. Startup reconcile recovers all content; only intra-debounce states are lost |
| Red icon fires so often it stops being read | Only the five error conditions in Section 8 colour it. Everything else — no `.notes` yet, retired worktrees, large captures — is a healthy condition and goes to the log, not the UI |

---

## 11. Deferred — local-only enforcement

The vault contains unfiltered secrets, and the thing that keeps that safe is that it never leaves
this machine. For now that is a property of how it is used, not something the app enforces.

Deferred to a later phase, listed here so the decision is recorded rather than forgotten:

- Refuse to start if the vault repo has any remote configured.
- Install a `pre-push` hook that exits non-zero unconditionally.
- Reject a vault path under a cloud-sync root (OneDrive, Dropbox, Drive, iCloud) — a synced vault
  is a remote with extra steps.
- Reject a vault path inside any watched worktree, which would otherwise recurse.

Until then: keep `C:\NoteVault` on a BitLocker volume, and do not add a remote to it.

---

## 12. Milestones

**Phase 1 — capture.** First-run setup (vault init, `README.md`, global gitignore), git-based
discovery, watchers, temp-file skip list, debounce, append-only mirror + commit with the five
correctness details from 5.4, full walk on discovery. Tray with Status / Open vault folder / Quit,
and the Status window. **`install.ps1` and `uninstall.ps1` ship in this phase, not later** — with
the graceful-shutdown handle (7.4) and the Defender exclusion flag — because an app that has to be
set up by hand is one you will not keep running. At the end of this phase `.notes` is protected,
and inspectable in Explorer or any git client.

**There is no retrieval phase.** That is the point of a git-backed vault: history, diff, and
restore already exist and are better than anything worth building here.

**Phase 2 — hardening.** Reconcile on startup / resume / watcher error, worktree retirement, the
red icon state, rolling logs, weekly `git gc`.

**Phase 3 — ergonomics (optional).** A VS Code extension listing note history for the active file
— reusing the tree UI in this repo with the vault as the backend. Worth it only if opening the
vault in a git client turns out to be friction in practice.

**Phase 4 — local-only enforcement.** Section 11.

There is no retention phase and no NTFS USN journal phase. Retention is `git gc`, and the USN
journal only ever earned its place by closing the downtime hole that the no-walk constraint
created — a hole that a 1 ms walk of a `.notes` folder now closes for free.

---

## 13. What was considered and dropped

The prior version of this plan proposed a custom content-addressed store — SHA-256 blobs, zstd,
a SQLite revision index, tiered retention thinning, and mark-and-sweep GC — because at whole-repo
scale no existing tool fit. Scoping to `.notes` removes the scale that justified it. Building
CAS + SQLite + retention + a query layer to obtain *less* than `git log` already provides is not
defensible for a few dozen kilobytes of text per worktree.

| Alternative | Why not |
|---|---|
| Kopia + a watcher shelling to `kopia snapshot` | Stat-walks the entire root per snapshot; snapshot-oriented, not per-file history |
| [dura](https://github.com/tkellogg/dura) | Commits the *working tree* of a repo; cannot capture gitignored files — i.e. exactly `.notes` |
| Syncthing versioning | Only archives versions received *from another device*; local-only edits are never versioned |
| FreeFileSync + RealTimeSync | Real-time and simple, but copy-per-version with no dedup and coarse retention |
| Custom CAS (prior plan) | Correct at whole-repo scale; strictly worse than git at `.notes` scale, especially for append-only logs |
| `.notes` as its own repo per worktree | Works, but N repos to manage and no cross-worktree view. One central vault is the same idea with one history |
