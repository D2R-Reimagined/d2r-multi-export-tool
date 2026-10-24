# Contributing to `d2r-multi-export-tool`

Thanks for your interest in contributing. This document is the entry
point for **human** contributors. Read it first; it links out to the
deeper docs only when you actually need them.

> Looking for the rules that apply to AI coding assistants (Claude,
> Junie, Copilot, Codex, …)? Those live in
> [`AGENTS.md`](./AGENTS.md). The conventions there apply to humans
> too. Root `CLAUDE.md` imports `AGENTS.md` using Claude Code's supported
> `@AGENTS.md` syntax; do not maintain a second standards document.

---

## Project scope (read this first)

`d2r-multi-export-tool` is a **purpose-built** data exporter. It reads a
D2R Reimagined mod source tree (`data\global\excel\*.txt` plus
`data\local\lng\strings\*.json`) and produces the key-based, language-
agnostic JSON bundle consumed by the companion
[D2R Reimagined website](https://github.com/D2R-Reimagined/d2r-reimagined-website).

It writes keyed content documents (`keyed/*.json`, with every player-facing
string as a translation key or `KeyedLine { key, args }`), per-locale string
bundles, WebP sprites, audit reports and a run manifest; see the
[output layout](README.md#output-layout).

The schema, CLI flags, and config shape exist to serve **that one
downstream consumer** and may change whenever the mod or the site need
them to. Contributions that broaden the tool's scope into a "general
D2R data dumper" are out of scope and will likely be declined.

---

## Repository layout

The project and folder responsibilities are listed in
[`AGENTS.md` §1](./AGENTS.md#1-purpose-and-architecture). Subsystem contracts live
under `docs/` and are indexed in [`AGENTS.md` §9](./AGENTS.md#9-detailed-references).

## Parser dependency

Excel `.txt` parsing is delegated to the
[`D2RReimaginedTools.FileExtensions`](https://www.nuget.org/packages/D2RReimaginedTools.FileExtensions)
NuGet package. Parser bugs are fixed **upstream** in the sibling
[`d2r-dotnet-tools`](https://github.com/D2R-Reimagined/d2r-dotnet-tools)
repo, not worked around inline here.

To debug an unreleased upstream fix locally, swap the
`<PackageReference Include="D2RReimaginedTools.FileExtensions" …/>`
in `D2RMultiExport.Lib/D2RMultiExport.Lib.csproj` for a local
`<ProjectReference>` pointing at your sibling `..\d2r-dotnet-tools`
checkout, validate via the smoke export, ship the upstream PR, then
bump the `<PackageReference>` `Version` once the upstream release is
out. **Revert the swap before committing** — the committed `.csproj`
must always restore from NuGet, and `dotnet restore .\D2RMultiExport.sln` is
required after package changes.

---

## Prerequisites

- **.NET SDK 10.0** (`global.json` pins `10.0.203` with
  `rollForward: latestFeature`).
- A D2R mod source tree with `data\global\excel\*.txt` and
  `data\local\lng\strings\*.json`.
- *(Recommended)* A CascView extraction of the base game's `data` tree,
  passed as `--casc-data`: it supplies vanilla sprites, base strings and
  tables the mod does not ship. See `README.md`.

Windows is the primary supported platform (the GUI is Avalonia and the
release workflow publishes `win-x64`). The library and console target portable
.NET 10, but Linux execution is not verified. Do not infer verified portability
from the target framework alone.

---

## Build and run

Restore + build the whole solution:

```powershell
dotnet restore .\D2RMultiExport.sln
dotnet build   .\D2RMultiExport.sln -c Release --no-restore
```

Run the [CLI](README.md#run--cli) (`export`) and the
[desktop GUI](README.md#run--gui) as described in the README, which is
authoritative for CLI options, source-root layouts and export modes.

---

## Tests and validation

[docs/TESTING.md](./docs/TESTING.md) is authoritative for validation levels,
commands, test selection and report review. Choose the level the change
requires (documentation-only changes need diff and link review, without builds
or exports). Record passed, failed, skipped and unexecuted checks in the PR; a
deterministic test pass alone does not establish required real-data validation.

---

## Coding conventions (summary)

The full ruleset is in [`AGENTS.md`](./AGENTS.md). The minimum a human
contributor needs in their head:

- **Target framework:** `net10.0`, **C# 14**, nullable reference types
  enabled. 4-space indent, file-scoped namespaces, Allman braces.
- **Naming:** `PascalCase` types/methods/properties, `camelCase`
  locals, `_camelCase` private fields, `*Set` suffix on
  config-derived hashsets.
- **Dictionaries / sets keyed by D2R codes** (`"cjw"`, `"rin"`, …)
  must use `StringComparer.OrdinalIgnoreCase`.
- **Async all the way down** — no `.Result` / `.Wait()`. Static
  loaders follow the `LoadAsync(path)` pattern.
- **Importers don't throw** for per-row failures; they record into
  `ImportResult<T>` via `AddError` / `AddWarning`. Only fatal load
  failures propagate.

### Two architectural rules contributors trip on most often

1. **No hardcoding.** New lookup data, codes, item/affix lists, or
   magic numbers belong in `D2RMultiExport.Lib/Config/*.json` (with a
   matching strongly-typed `*Config.cs` property), **not** inline in
   importer or exporter code. The full "no-hardcoding" rule and its
   canonical pattern are in `AGENTS.md`.
2. **Exports are keyed.** Every player-facing string written under
   `<out>/keyed/*.json` must be a translation key (or a `KeyedLine
   { key, args }`) — never a baked English sentence. Three documented
   raw-English cases (cube recipe `Description`, the `PType`
   Prefix/Suffix discriminator, `RequiredClass`) are listed in
   `AGENTS.md`; new exceptions require an explicit decision.

If your change introduces a brand-new player-facing string with no
matching CASC translation key, register it in
`D2RMultiExport.Lib/Config/synthetic-strings.json` so the language
bundles include it.

---

## Pull-request workflow

1. **Fork and branch** from `main`. Keep branches focused; one logical
   change per PR.
2. **Conventional Commits.** At least one commit on the PR must
   follow:
   ```
   type(scope?)!?: short, lowercase description
   ```
   Allowed types: `feat | fix | docs | style | refactor | perf | test
   | build | ci | chore | revert`. Use `!` (e.g. `feat!:`) or a
   `BREAKING CHANGE:` body paragraph for breaking changes. The
   **Validate Commits** workflow (`pr-semantic-commits.yml`, status
   context `validate-commits`) enforces this on pull requests targeting
   `main` (or `master`); squash-
   merging with the PR title as the commit subject is the intended
   workflow.
3. **Validate locally before pushing** at the appropriate level in
   [docs/TESTING.md](./docs/TESTING.md), including Level C when the change's
   scope requires it; reuse an already passing matching build/test session.
   The **Build Check** workflow (`ci.yml`) is **manual-only**
   (`workflow_dispatch`); trigger it from **Actions → Build Check → Run
   workflow** before merge for a clean-runner confirmation. It builds Release
   and runs deterministic C#/Python suites, without private real-data inputs.
4. **Don't leave a local `<ProjectReference>` swap** for
   `D2RReimaginedTools.FileExtensions` in `D2RMultiExport.Lib.csproj`
   — the committed `.csproj` must always restore from NuGet.
5. **Fill out the PR template** (validation level, commands/results and any
   skipped or unexecuted checks). It's not enforced, but it shortens review.

---

## Where to file things

- **Bug reports and feature requests** → GitHub Issues (the repository
  provides only a blank issue template). For bugs, include the exporter
  version or commit, the command/options used and the relevant
  `extras\import-report.txt` lines. Note the project scope above; requests
  outside it will be declined with a pointer back here.
- **Questions and support requests** → GitHub Issues (GitHub Discussions is
  not enabled for this repository).
- **Security concerns** → **do not open a public issue.** Use
  [GitHub private vulnerability reporting](https://github.com/D2R-Reimagined/d2r-multi-export-tool/security/advisories/new);
  see [`SECURITY.md`](./SECURITY.md) for the threat model and what to include.
- **Code-of-conduct concerns** → see [`CODE_OF_CONDUCT.md`](./CODE_OF_CONDUCT.md)
  for the reporting channel.

---

## Releases

Tagged releases are produced manually from
**Actions → Release → Run workflow**; the procedure, package contents,
versioning, assembly metadata and application-icon branding are in
[`docs/RELEASING.md`](./docs/RELEASING.md). Release notes are generated from
Conventional-Commits subjects; **there is no `CHANGELOG.md`** — the
[GitHub Releases page](https://github.com/D2R-Reimagined/d2r-multi-export-tool/releases)
is the canonical changelog.

---

## License

By contributing, you agree that your contributions will be licensed
under the [GNU GPL-3.0-or-later](./LICENSE), the same license that
covers the rest of the project. Source files carry an
`// SPDX-License-Identifier: GPL-3.0-or-later` header; please keep
that header on any new `.cs` file you add.
