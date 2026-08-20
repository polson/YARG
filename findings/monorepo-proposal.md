# Migrating YARG.Core to YARG (Monorepo)

## Table of contents

- [Summary](#summary)
- [Current structure](#current-structure)
- [What is bad about YARG.Core and how a monorepo fixes it](#what-is-bad-about-yargcore-and-how-a-monorepo-fixes-it)
- [What to do with the YARG.Core repo](#what-to-do-with-the-yargcore-repo)
- [Risk assessment](#risk-assessment)
- [Migration steps](#migration-steps)
  - [Make the PR](#1-make-the-pr)
  - [Merge](#2-merge)
  - [Archive YARG.Core](#3-archive-yargcore)
  - [Update your clone (after merge)](#4-update-your-clone-after-merge)
  - [Handle open PRs (after merge)](#after-the-monorepo--open-prs)
- [How to undo the merge](#how-to-undo-the-merge)

## Summary

Import YARG.Core into YARG as a normal folder, then archive the YARG.Core repo.

1. Import Core's files into `YARG/YARG.Core` as one commit — Core's history stays in the archived repo, browsable and cloneable
2. Point Unity at the new folder
3. Delete the submodule hacks
4. Update the docs
5. Merge the PR, then archive `YARC-Official/YARG.Core`
6. Move open Core PRs into YARG and inform contributors how to update their clone

## Current structure

```
YARC-Official/YARG          <- main Unity project
  YARG.Core/                <- not a normal folder, it's a link to the other repo
  .gitmodules               <- says where that link points
  Packages/manifest.json    <- tells Unity: load Core from "../YARG.Core/YARG.Core"
  Assets/Plugins/Editor/Submodule/
    ProjectAdder.cs         <- hacks Unity's solution file so Rider/VS can see Core
    SubmoduleHelper.cs      <- runs "git rev-parse HEAD" to check if Core changed
```

## What is bad about YARG.Core and how a monorepo fixes it

### 1. Submodules make git confusing

* A plain `git clone` doesn't clone everything. `YARG.Core/` is just left empty.  You have to remember `git submodule update --init --recursive`
  * *After:* `git clone dev https://github.com/YARC-Official/YARG.git` just works

* If you commit inside `YARG.Core/` without creating a branch, it's easy to lose work
  * *After:* It's just a normal folder on a normal branch. You can edit `YARG.Core/Chart/ChartParser.cs` like any other file.

* `git status` just says `modified: YARG.Core (new commits)` with no details. You need `git diff --submodule=diff` to see what actually changed. 
  * *After:* `git status` shows all actual file changes together

### 2. Work takes longer

* Most changes touch both repos. You open a PR in Core and a PR in YARG, wait for two reviews, maintainers merge the Core PR, then YARG PR, then switch to `dev` and update the core pointer and push.
  * *After:* One PR with everything together. One review, one merge

* `git log` is full of `Core pointer update for xyz`. `Git log` doesnt show what's in these changes.  We have to change repos to figure it out.
  * *After:* One clean history. `git blame YARG.Core/Engine/EngineManager.cs` would show the full changes.

* A bug caused by a change in both repos can't easily be found with `git bisect`  And reverting a change needs three steps: revert YARG, revert Core, push a new core pointer.
  * *After:* `git bisect` works, reverting with `git revert` is easy

### 3. Submodules require special handling in Unity and IDE

* Unity rewrites `*.sln` and `*.csproj` on every reload. To make Rider and VS see Core, `ProjectAdder.cs` does some magic to patch Core projects back in. It needs an undocumented Unity hook.  We also have a hack in `SubmoduleHelper.cs` that checks if Core changed.
  * *After:* We can delete the whole `Assets/Plugins/Editor/Submodule/` folder

* `.editorconfig` and build props exist in both repos and can drift.
  * *After:* One editor config for both

### 4. Author credits are missing

* `#git-tracker` in Discord and also in the Nightly logs doesn't give credit for YARG.Core changes. The maintainer who pushes the YARG.Core pointer has to put the credit and the description inside the commit message.
  * *After:* `#git-tracker`, and Nightly logs credit the author and show the changes

## What to do with the YARG.Core repo

Archive it. GitHub's archive makes the repo read-only in one click: Settings > Danger Zone > Archive this repository. The repo stays fully browsable and cloneable, so:

* Community tools that clone YARG.Core keep working — clones are read-only anyway.
* Core's history stays accessible — the archived repo keeps every commit; nothing is deleted.
* Open Core PRs can no longer merge, so work moves to YARG.

If a community tool needs a Core fix after the archive, the fix lands in YARG, and anyone can get it from the monorepo.

## Risk assessment

* **YARG history: zero risk.** Every step is a normal, append-only commit; the merge is a plain PR merge, and undoing it is a plain revert. Nothing force-pushes or rewrites history.
* **YARG.Core repo: zero risk.** Archiving deletes nothing and is reversible with one click. History, issues, and clones keep working.
* **No automation to break.** No CI workflow, no branch protection — there is nothing that can go stale or crash.

## Migration steps

### 1. Make the PR

1. **Import Core's files into YARG.** This step turns the submodule link into a real folder — as a single import commit, not Core's full history (Core's history stays in the archived repo).

   From the **YARG** repo:
   ```bash
   git checkout -b monorepo-merge dev
   # The submodule must be removed first
   git submodule deinit -f YARG.Core
   git rm -f YARG.Core .gitmodules
   git commit -m "Remove YARG.Core submodule"
   git remote add yarg-core https://github.com/YARC-Official/YARG.Core.git
   git fetch yarg-core
   # import Core's files as one commit
   git read-tree --prefix=YARG.Core/ yarg-core/master^{tree}
   git commit -m "Import YARG.Core into monorepo"
   ```
   This adds Core's files as a single import commit — the ~2k core commits do not enter YARG's history:
   * `git log` stays YARG's own history plus the import and merge commits — no core commits mixed in.
   * The existing `Core pointer update...` commits stay in YARG's history.
   * Core's history stays browsable and cloneable in the archived repo — nothing is lost.
2. **Verify in Unity.** Open the project in Unity and check it compiles with no errors. In batch mode:
   ```bash
   Unity.exe -batchmode -nographics -quit \
     -projectPath "$PWD" -logFile unity-import.log
   ```
3. **Run Core's test suite.** Core's real tests are `dotnet test`, not the Unity Test Runner (the `YARG.Core.UnitTests` folder is outside the UPM package, so Unity never compiles it):
   ```bash
   cd YARG.Core
   dotnet restore YARG.Core.sln
   dotnet test YARG.Core.sln --configuration Debug --no-restore
   ```
4. **Delete the submodule editor hacks.**
   ```bash
   git rm -r Assets/Plugins/Editor/Submodule
   ```
   The folder patched Unity's `*.sln` so Rider/VS could see Core and checked if the pointer changed. Now it is no longer needed.
5. **Update README.md and CONTRIBUTING.md**
   ```diff
   - git clone -b dev --recursive https://github.com/YARC-Official/YARG.git
   - git submodule update --init --recursive
   + git clone -b dev https://github.com/YARC-Official/YARG.git
   ```
6. **Open the PR** from `monorepo-merge` to `dev`.

### 2. Merge

Merge the PR with the normal GitHub merge button. It is a plain merge commit like any other.

### 3. Archive YARG.Core

1. Before archiving: if Core's CI publishes NuGet/UPM packages from the old repo, move that job to YARG first (check `.github/workflows` in YARG.Core for release jobs).
2. Archive: `YARC-Official/YARG.Core` > Settings > Danger Zone > Archive this repository.
3. Verify: the repo shows the archived banner; `git clone` still works; the history is still browsable.

Archiving makes the repo read-only: no pushes, no new PRs. Issues and clones keep working, and unarchiving is one click if it is ever needed.

### 4. Update your clone (after merge)

After `dev` merges, run in your existing clone:
```bash
git checkout dev
git submodule deinit -f YARG.Core
rm -rf .git/modules/Assets/Plugins/YARG.Core
git pull
```

<a id="after-the-monorepo--open-prs"></a>
### 5. Handle open PRs (after merge)

After we archive the YARG.Core repo, PRs can no longer merge. GitHub will show a greyed out merge button. All Core work will have to be migrated to the `YARG` repository. This can be done by maintainers without losing git authorship.

* **Single Core PR** - close the old Core PR and open a new one in `YARG` under `YARG.Core/`
* **Paired YARG + Core feature** - keep the YARG PR open and push the Core changes into that same branch, so one YARG PR has both. Then close the Core PR with a link to the YARG PR.

To do the move, first export it as a patch file
```bash
# In your YARG.Core checkout, on your feature branch
git checkout my-core-feature
# format-patch saves commits as patch files for any changes not on master
git format-patch origin/master --stdout > ~/my-core-pr.patch
```

*For a single Core PR - create a new branch:*
```bash
# new branch from dev
git checkout -b my-feature dev
# replay commits keeping original author
git am --directory=YARG.Core/ ~/my-core-pr.patch
git push
```
*For a paired YARG + Core feature - add to the existing YARG PR branch:*
```bash
# switch to your existing YARG PR branch
git checkout existing-yarg-pr-branch
# clear the submodule worktree first: merging dev replaces the gitlink with a
# folder, and the checked-out submodule files block the merge
# ("untracked working tree files would be overwritten")
git submodule deinit -f YARG.Core
# merge the new dev in first: the branch still has the old submodule link, and
# git am refuses to add files under it ("appears as both a file and as a directory")
git merge dev
# resolve conflicts by keeping dev's versions (.gitmodules is deleted, YARG.Core becomes a normal folder)
# replay commits keeping original author
git am --directory=YARG.Core/ ~/my-core-pr.patch
git push
```

## How to undo the merge

The merge is a plain merge commit, so undo it like any other PR:

1. **Revert post-merge commits first.** If anything landed on `dev` after the merge and touched `YARG.Core/`, revert those commits first (newest first). A revert of the merge deletes the whole `YARG.Core/` folder; if a later commit changed files in it, the deletion conflicts with those changes.
2. **Revert the merge commit itself** — GitHub's Revert button on the merged PR, or:
   ```bash
   git revert -m 1 <merge commit>
   ```
   A merge commit has two parents, so git needs `-m` to know which side to keep: `-m 1` keeps parent 1 — `dev` as it was before the merge — and undoes everything the merge introduced. After this, `dev` is back to its pre-merge state and `YARG.Core` is a gitlink again.

If the old repo is ever needed writable again, unarchive it (one click, Settings > Danger Zone > Unarchive this repository).
