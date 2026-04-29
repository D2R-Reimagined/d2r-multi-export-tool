# Releasing

Releases are produced by the manual GitHub Actions workflow
[`.github/workflows/release.yml`](../.github/workflows/release.yml).
The workflow publishes the CLI and GUI side-by-side as a self-contained
single-file `win-x64` build, zips them with the bundled `Config\` tree,
tags the commit, and creates a GitHub Release with auto-generated
release notes grouped by Conventional-Commit type.

## Triggering a release

1. Go to **Actions → Release → Run workflow** in the GitHub UI.
2. Pick the branch (normally `main`).
3. Enter the **version** input — SemVer, no leading `v`. Examples:
   - `0.3.1`
   - `0.4.0-rc.1`
   - `1.0.0`
4. Click **Run workflow**.

The workflow validates the version against
`^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$` and aborts on mismatch. It
also aborts if the resulting tag (`v<version>`) already exists on the
remote.

## What ends up in the release

The published GitHub Release `v<version>` contains:

- `D2RMultiExport-v<version>-win-x64.zip` — the runnable bundle.
- Release notes auto-generated from commits between the previous tag
  and `v<version>` (categories: Features / Fixes / Performance /
  Refactoring / Documentation / Tests / Build / CI / Chores / Reverts /
  Breaking Changes — driven by Conventional-Commit type and the `!`
  breaking marker / `BREAKING CHANGE:` body trailer).

Zip contents (top-level folder: `D2RMultiExport\`):

```
D2RMultiExport\
├── D2RMultiExport.Console.exe       # CLI entry point
├── D2RMultiExport.Client.exe        # Avalonia GUI entry point
├── *.runtimeconfig.json, *.dll …    # self-contained .NET 10 runtime + native bundles
├── Config\
│   ├── export-config.json
│   ├── synthetic-strings.json
│   ├── stat-overrides.json
│   ├── class-ranges.json
│   └── CASC_DATA\runes.txt
├── LICENSE.txt                      # GPL-3.0
└── README.txt                       # short "how to run" pointer back to the repo
```

Both `.exe` files share the same `Config\` folder, so end-users can
run either entry point from inside the extracted directory without
extra setup.

## Pre-flight checklist

Before triggering a release, confirm:

- [ ] `main` is green on the **CI** workflow (sanity build).
- [ ] The smoke export from `docs\TESTING.md` runs cleanly against the
      current Reimagined mod tree (`Export completed successfully.`,
      no new errors in `extras\import-report.txt`).
- [ ] The PR(s) merged since the last tag contain at least one
      well-formed Conventional Commit message — those subjects feed the
      release notes generator. Squash-merge with the PR title gives you
      this for free, provided the **PR Semantic Commits** check passed.

## Versioning policy

- SemVer 2.0.
- Pre-1.0 minor bumps may include breaking changes — call them out with
  `feat!: …` / `fix!: …` so the generator places them under
  **Breaking Changes**.
- The `<Version>` written into `Directory.Build.props` is the
  development default (`0.0.0`); the workflow overrides it with
  `-p:Version=<input>` at publish time, so committed changes to
  `Directory.Build.props` are not required for a release.

## Troubleshooting

- **`Tag v<x> already exists`** — bump the patch and retry, or delete
  the stale tag manually if it was created by an aborted run.
- **Avalonia GUI fails to launch from the single-file exe** — double-
  check `IncludeNativeLibrariesForSelfExtract=true` is still present
  in the publish step. Avalonia's `libHarfBuzzSharp` / `libSkiaSharp`
  natives must be self-extracted at startup.
- **`Publish output is missing required files: Config\…`** — the
  guard step caught a regression in `D2RMultiExport.Lib.csproj`'s
  `<None Update="Config\…">` `CopyToOutputDirectory` rules. Fix the
  csproj, not the workflow.
