# Releasing

Windows Audio Recorder uses a two-branch Gitflow: `develop` is where work lands day to day,
`main` is always the latest released state, and a release is nothing more than a tag pushed on
`main`. This document is the checklist for cutting one.

## Branches

| Branch | Purpose |
|---|---|
| `develop` | Default branch. All feature/fix branches merge here via PR. Always green (CI runs on every push, see [`.github/workflows/ci.yml`](.github/workflows/ci.yml)), but not necessarily release-ready. |
| `main` | Always matches the most recent release. Nothing is pushed here except a merge from `develop` (or a hotfix branch) immediately followed by a tag. |
| `feature/*`, `fix/*` | Cut from `develop`, merged back into `develop` via PR. Short-lived. |
| `hotfix/*` | Cut from `main` for an urgent fix that can't wait for `develop` to be release-ready. See [Hotfixes](#hotfixes). |

```
feature/x ──┐
            ├──► develop ──► (release PR) ──► main ──► tag vX.Y.Z ──► GitHub Release
fix/y ──────┘                                  ▲
                                    hotfix/z ───┘ (then back-merged into develop)
```

## Cutting a normal release

1. **Confirm `develop` is ready.** Every push to `develop` runs
   [`ci.yml`](.github/workflows/ci.yml), which builds `-warnaserror` and runs the same
   self-contained publish the release does. Make sure the latest run is green.

2. **Merge `develop` into `main`.** Open a PR `develop` → `main` (preferred, gives you a final
   review + a green CI check on the merge itself) or merge locally:
   ```bash
   git checkout main
   git pull origin main
   git merge --ff-only develop   # fails loudly if main has diverged; see below if it does
   git push origin main
   ```

3. **Bump the version.** Update `<Version>` in
   [`WindowAudioRecorder.csproj`](WindowAudioRecorder.csproj) to match the release you're about
   to tag, commit it on `main` (or include it in the release PR from step 2). `release.yml`
   also passes `-p:Version=` at publish time, so the shipped binaries get the right version
   even if this is missed, but keep the checked-in value in sync so local builds and released
   ones agree.

4. **Tag `main` and push the tag:**
   ```bash
   git checkout main
   git pull origin main
   git tag -a v1.0.0 -m "v1.0.0"
   git push origin v1.0.0
   ```
   Pushing the tag triggers [`release.yml`](.github/workflows/release.yml), which builds the
   Windows MSI and the portable zip and publishes them as a GitHub Release with auto-generated
   notes.

5. **Merge `main` back into `develop`** so the version bump (and anything else that landed
   directly on `main`) isn't lost:
   ```bash
   git checkout develop
   git merge main
   git push origin develop
   ```

6. **Check the release.** The workflow takes a few minutes. When it's done, confirm both assets
   are attached to the release and that the MSI installs and launches.

### Why the tag has to be on `main`

Git tags aren't tied to a branch. Pushing `v1.0.0` from `develop` or a stray feature branch
would trigger `release.yml` just as well as tagging `main`. Rather than rely on everyone
remembering the convention, `release.yml` has a `verify-tag-on-main` job that runs first and
checks the tagged commit is actually an ancestor of `origin/main`; the `windows` and `release`
jobs `need` it and won't start if it fails. Tag the wrong branch and the workflow fails fast
with a clear error instead of quietly shipping a release built from unreviewed code.

## Hotfixes

For a fix that can't wait for `develop` to reach release-ready state:

```bash
git checkout -b hotfix/short-description main
# fix, commit
git checkout main
git merge --no-ff hotfix/short-description
git push origin main
git tag -a v1.0.1 -m "v1.0.1"
git push origin v1.0.1
git checkout develop
git merge main
git push origin develop
```

Same shape as a normal release (merge into `main`, tag, back-merge into `develop`). The only
difference is that the fix branches from `main` instead of `develop`, so it ships without
pulling in whatever else is mid-flight on `develop`.

## Versioning

Tags follow [SemVer](https://semver.org/): `vMAJOR.MINOR.PATCH` (e.g. `v1.0.0`). The `v*`
pattern in `release.yml` matches any tag starting with `v`, so pre-releases like `v1.1.0-rc.1`
also trigger a build; release notes are auto-generated (`--generate-notes`), so tag message and
version bump are still worth getting right, but nothing bespoke is needed to support them.

Windows Installer only understands a four-part numeric version and rejects SemVer's
pre-release suffix, so `release.yml` derives the MSI's `ProductVersion` from the tag: it strips
any `-rc.1`, then pads or trims to exactly four parts. Tag `v1.0.0` and the MSI is
`ProductVersion 1.0.0.0`; `v1.1.0-rc.1` becomes `1.1.0.0`.

> Two pre-releases of the same version (`v1.1.0-rc.1` and `v1.1.0-rc.2`) therefore collapse to
> the same MSI `ProductVersion`, so Windows Installer sees them as the same build and won't
> treat the second as an upgrade. That's fine for testing a single RC; bump the patch number if
> you need two installable candidates back to back.

## Building the installer locally

To reproduce what CI produces, without pushing a tag:

```powershell
pwsh Packaging/windows/build-local.ps1 -Version 1.0.0
```

Output lands in `artifacts/dist/` (gitignored). The script installs the WiX v7 dotnet tool if
it isn't already present.

## Signing

Neither the MSI nor the .exe is code-signed. SmartScreen will show a "Windows protected your
PC" prompt on first run until the download builds reputation. Users click *More info* > *Run
anyway*. Signing needs a paid certificate; if one is obtained, add a `signtool` step to the
`windows` job in `release.yml` after the publish and before the MSI build, and sign the MSI
after `wix build`.

## Quick reference

| I want to... | Do this |
|---|---|
| Ship what's on `develop` | Merge `develop` → `main`, bump version, tag `main`, push tag |
| Ship an urgent fix now | Branch `hotfix/*` from `main`, merge to `main`, tag, back-merge to `develop` |
| Build an installer without releasing | `pwsh Packaging/windows/build-local.ps1 -Version X.Y.Z` |
| Run a build without releasing | Just push. [`ci.yml`](.github/workflows/ci.yml) runs on every push/PR, any branch |
| Re-run a release | Delete the tag locally and remotely, fix the problem, re-tag, re-push |
