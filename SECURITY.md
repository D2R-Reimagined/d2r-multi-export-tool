# Security policy

## Reporting a vulnerability

**Please do not open a public GitHub issue for security reports.**

Use **[GitHub private vulnerability reporting](https://github.com/D2R-Reimagined/d2r-multi-export-tool/security/advisories/new)**
instead. That channel is private to the repo's security responders and
keeps the report out of the public timeline until a fix and disclosure
plan are in place.

When reporting, please include:

- The affected component (`D2RMultiExport.Lib`,
  `D2RMultiExport.Console`, `D2RMultiExport.Client`, the release ZIP,
  the bundled `Config\` tree, or the `manifest.json` produced by an
  export).
- The version (release tag, or commit SHA if reporting against `main`).
- A minimal reproduction or proof of concept where possible.
- The impact you observed or believe is possible.

You should expect an initial acknowledgement within roughly **7
calendar days**. Resolution timelines vary with severity; we will keep
the advisory updated as the fix progresses, and we will coordinate the
public disclosure with you before publishing.

## Supported versions

| Version | Supported |
|---|---|
| Latest tagged release on `main` | ✅ |
| Older tagged releases | ❌ — please upgrade |
| `main` between releases | Best-effort, not formally supported |

Security fixes ship in the next tagged release. There are no separate
patch branches for older versions.

## Threat model (what to look for)

This is a **build-time data exporter** that runs locally on a
contributor's or release-builder's machine. It is not a service, does
not accept network input, does not authenticate users, and does not
process untrusted data at runtime. The realistic security surface is
therefore narrow but non-empty:

1. **Supply-chain integrity of the export bundle.** The whole value
   proposition of `manifest.json` is the per-file SHA-256 hashes —
   the downstream
   [D2R Reimagined website](https://github.com/D2R-Reimagined) trusts
   those hashes. Anything that lets an attacker silently shift those
   hashes (transitive NuGet swap, build non-determinism, unreviewed
   changes to the bundled `Config\` tree, or compromise of the
   release workflow) is in scope. The repo mitigates this with
   deterministic builds (`<Deterministic>true</Deterministic>` in
   `Directory.Build.props`), an SDK pinned via `global.json`, the
   per-file SHA-256 chain written into `manifest.json` on every
   export, and Dependabot PRs (`.github/dependabot.yml`) that surface
   every NuGet and `actions/*` version shift for review before it
   lands on `main`.
2. **Supply-chain integrity of the upstream parser.** Excel `.txt`
   parsing is delegated to the
   [`D2RReimaginedTools.FileExtensions`](https://www.nuget.org/packages/D2RReimaginedTools.FileExtensions)
   NuGet package. Vulnerabilities in that package are in scope to the
   extent they affect this project's output; please also report them
   upstream at
   [`d2r-dotnet-tools`](https://github.com/D2R-Reimagined/d2r-dotnet-tools/security/advisories/new).
3. **Path / file handling in the importer and exporter.** Anything
   that lets a maliciously crafted mod source tree write outside the
   user-specified `--out` directory, traverse with `..`, follow
   symlinks unexpectedly, or DoS the tool with pathological input.
4. **Release-artifact integrity.** Anything that affects the
   contents of the published `D2RMultiExport-v<version>-win-x64.zip`
   beyond what the source diff would suggest (release-workflow
   compromise, tampered icons, bundled `Config\` drift not reflected
   in the source tree).

The following are **explicitly out of scope** as security issues
(open a regular bug instead):

- Wrong or missing translation keys, formatting glitches in
  `keyed/*.json`, or audit-only warnings under `extras/`.
- "The exporter crashed on a malformed mod file" without a
  reproducible path-traversal, code-execution, or integrity impact —
  that's a robustness bug, not a vulnerability.
- Performance regressions and large memory use.
- Build failures unrelated to dependency tampering.

## Verifying release artifacts

Each release ships a single self-contained `win-x64` zip. To verify
you have the artifact the release workflow produced:

1. Run an export against your mod source.
2. Compare the per-file SHA-256 hashes the tool wrote to
   `<out>/manifest.json` against the hashes published by the
   downstream consumer for the same release.

If the hashes diverge without a corresponding source-code change in
the diff between releases, treat it as a possible supply-chain
incident and report via the channel above.
