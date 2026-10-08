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
not accept network input or authenticate users. It parses user-supplied local
mod/CASC files, which can be malformed or crafted. The realistic security surface is
therefore narrow but non-empty:

1. **Supply-chain integrity of the export bundle.** Every export
   writes `<out>/manifest.json` last (`ExportOutputs.WriteManifestAsync`):
   the SHA-256 and byte length of each file *that run* wrote, the
   status of each export step, `Succeeded: false` whenever the run
   recorded an error and `Complete: false` whenever, in addition, a
   step was skipped or left a declared output unwritten. Files left in
   `--out` by an earlier run or another tool are never listed. The
   previous manifest is deleted at the start of the output phase. A failure
   before that phase leaves the previous bundle and manifest untouched; a
   failure during output before manifest completion leaves no new manifest.
   Hashes do not attest code/source versions or freshness. Anything that lets an attacker silently shift those
   hashes (transitive NuGet swap, build non-determinism, unreviewed
   changes to the bundled `Config\` tree, or compromise of the
   release workflow) is in scope. The repo mitigates this with
   deterministic builds (`<Deterministic>true</Deterministic>` in
   `Directory.Build.props`) and an SDK pinned via `global.json`.
   What the manifest does **not** do: it is not signed. When checked, its hashes detect
   accidental drift and partial or mixed bundles, not a party able to
   rewrite both the files and the manifest. Signing is deliberately not
   implemented: the exporter runs on a trusted build machine and the
   bundle is published through the repository's own access control. Revisit
   this if that threat model changes. The manifest is an export/publishing
   integrity mechanism, not mandatory browser verification or additional
   website requests (see [WEBSITE-INTEGRATION.md](docs/WEBSITE-INTEGRATION.md#manifest-schema-2-and-publishing)).
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
   Every exporter writes through `ExportOutputs`, which resolves each
   path under `--out` and refuses a rooted path, one that escapes it
   (e.g. a sprite asset name containing `..`; compared ordinally so
   case cannot be used to escape on a case-sensitive file system), and
   one whose existing components below `--out` include a symbolic link
   or junction. The same check guards every deletion, and the tool only
   ever deletes files a failed step attempted in the same run plus the
   previous `manifest.json`. Abandoned-temp cleanup only examines the exact
   same-output temp-name pattern in that file's directory; it never sweeps
   unrelated files. The
   `--out` directory itself may be a link (it is the user's choice).
   On Windows/Linux the opened temp-file handle's real path is checked against
   the real export root, and the destination is checked again before rename.
   Known limit: the last check and rename are not atomic; a concurrent link
   swap in that interval can escape those checks. Other platforms retain path
   checks only. `--out` is assumed to be writable only by the exporting user.
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

## Verifying exported bundles

Each release ships a self-contained `win-x64` zip. An export manifest verifies
the resulting payload; it does not authenticate that executable or ZIP:

1. Run an export against your mod source and confirm
   `<out>/manifest.json` reports `"Succeeded": true` and `"Complete": true`
   (`Complete` is false when an optional exporter was skipped).
2. Use `publish --from <out> --staging <new-or-empty-dir>` to verify copied
   payload hashes/lengths against the captured schema-2 manifest.
3. For reproducibility, compare payload entries with a trusted build using
   identical code, mod/CASC inputs and configuration. Investigate differences
   in those inputs before treating divergent hashes as evidence of tampering.

Report a reproducible integrity discrepancy with a suspected security impact
through the private channel above.


Publish staging resolves physical directory roots on Windows and Linux,
including links in existing parents of a new staging path. Failure to obtain
those physical paths refuses staging on these platforms. Other platforms retain
lexical containment checks; physical-root verification is not implemented there.
The source payload bytes are hashed while copying and compared to a captured
manifest, which is committed last. These checks do not claim an atomic snapshot
of filesystem namespaces against an adversary changing directory links during
execution. Manifests remain unsigned local export records.
