# Contributing to `d2r-multi-export-tool`

Thanks for your interest in contributing. This document is the entry
point for **human** contributors. Read it first; it links out to the
deeper docs only when you actually need them.

> Looking for the rules that apply to AI coding assistants (Claude,
> Junie, Copilot, Codex, …)? Those live in
> [`AGENTS.md`](./AGENTS.md). The conventions there apply to humans
> too, but the structure of that file is optimized for agents.

---

## Project scope (read this first)

`d2r-multi-export-tool` is a **purpose-built** data exporter. It reads a
D2R Reimagined mod source tree (`data\global\excel\*.txt` plus
`data\local\lng\strings\*.json`) and produces the key-based, language-
agnostic JSON bundle consumed by the companion
[D2R Reimagined website](https://github.com/D2R-Reimagined).

Concretely, the tool exists to produce:

- `<out>/keyed/*.json` — one file per content family
  (uniques, sets, runewords, magic affixes, cube recipes, base items,
  etc.), with every player-facing string represented as a translation
  **key** or a `KeyedLine { key, args }` object.
- `<out>/strings/<lang>.json` — flat `{ key: "localized text" }` map
  per supported D2R locale.
- `<out>/extras/` — audit folder (import report, missing-translations
  audit, etc.).

The schema, CLI flags, and config shape exist to serve **that one
downstream consumer** and may change whenever the mod or the site need
them to. Contributions that broaden the tool's scope into a "general
D2R data dumper" are out of scope and will likely be declined.

---

## Repository layout (high level)

| Path | Purpose |
|---|---|
| `D2RMultiExport.Lib/` | Core import → translation → export pipeline. |
| `D2RMultiExport.Lib/Config/` | Bundled JSON config + matching `*Config.cs` POCOs. |
| `D2RMultiExport.Lib/Import/` | One importer per `.txt` family (uniques, sets, runewords, cube recipes, …). |
| `D2RMultiExport.Lib/Translation/` | Property → translation-key resolution, synthetic-string registry. |
| `D2RMultiExport.Lib/Exporters/` | Final JSON writers (`keyed/`, `strings/`). |
| `D2RMultiExport.Console/` | Headless CLI (`export` command). |
| `D2RMultiExport.Client/` | Avalonia desktop GUI front-end. |
| `docs/` | Operational docs (`TESTING.md`, `RELEASING.md`). |

Excel `.txt` parsing is delegated to the
[`D2RReimaginedTools.FileExtensions`](https://www.nuget.org/packages/D2RReimaginedTools.FileExtensions)
NuGet package. Parser bugs are fixed **upstream** in the sibling
[`d2r-dotnet-tools`](https://github.com/D2R-Reimagined/d2r-dotnet-tools)
repo, not worked around inline here. See
[`AGENTS.md` → *External parsing dependency*](./AGENTS.md) for the
local-debug `<ProjectReference>` swap recipe.

---

## Prerequisites

- **.NET SDK 10.0** (`global.json` pins `10.0.203` with
  `rollForward: latestFeature`).
- A D2R mod source tree with `data\global\excel\*.txt` and
  `data\local\lng\strings\*.json`.
- *(Optional)* A CASC dump of the base game's `local\lng\strings`
  if you want to fill missing translations. See `README.md` for the
  `--base-strings` flag.

Windows is the primary supported platform (the GUI is Avalonia and the
release workflow publishes `win-x64`). The library and console build
on any .NET 10 platform.

---

## Build and run

Restore + build the whole solution:

```powershell
dotnet restore .\D2RMultiExport.sln
dotnet build   .\D2RMultiExport.sln -c Release --no-restore
```

Run the headless export (CLI):

```powershell
dotnet run --project .\D2RMultiExport.Console -c Release -- export `
    --mod-root "<path-to-mod>" `
    --out      .\test-output
```

Run the desktop GUI:

```powershell
dotnet run --project .\D2RMultiExport.Client -c Release
```

---

## Smoke test (the de-facto test suite)

There is no automated unit-test project today. The end-to-end smoke
export documented in [`docs/TESTING.md`](./docs/TESTING.md) **is** the
project's test suite. Any change that touches the import, translation,
or export pipeline must be validated by running it.

A successful smoke export prints `Export completed successfully.` and
leaves a clean `extras/import-report.txt` (no new errors). For
refactors, snapshot `test-output/` to `test-output-baseline/` first and
diff `keyed/*.json` after to confirm parity.

Documentation-only or comment-only changes can skip the smoke export;
say so explicitly in your PR description so reviewers don't waste time
re-running it.

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
   structural exceptions (cube recipe `Description`, the `PType`
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
   `pr-semantic-commits` workflow enforces this on every PR; squash-
   merging with the PR title as the commit subject is the intended
   workflow.
3. **CI must pass.** `dotnet build .\D2RMultiExport.sln` is the same
   command CI runs.
4. **Smoke-test the export** if you touched the pipeline.
5. **Don't leave a local `<ProjectReference>` swap** for
   `D2RReimaginedTools.FileExtensions` in `D2RMultiExport.Lib.csproj`
   — the committed `.csproj` must always restore from NuGet.
6. **Fill out the PR template.** It's not enforced, but it shortens
   review.

---

## Where to file things

- **Bug reports** → GitHub Issues, `Bug report` template.
- **Feature requests** → GitHub Issues, `Feature request` template.
  Note the project scope above; requests outside it will be declined
  with a pointer back here.
- **Questions / discussion** → GitHub Discussions.
- **Security vulnerabilities** → **Do not open a public issue.**
  Use [GitHub private vulnerability reporting](https://github.com/D2R-Reimagined/d2r-multi-export-tool/security/advisories/new).
  See [`SECURITY.md`](./SECURITY.md).
- **Code-of-conduct concerns** → see [`CODE_OF_CONDUCT.md`](./CODE_OF_CONDUCT.md)
  for the reporting channel.

---

## Releases

Tagged releases are produced manually from
**Actions → Release → Run workflow**. Full procedure in
[`docs/RELEASING.md`](./docs/RELEASING.md). The release notes are
auto-generated from Conventional-Commits subjects on the merged PRs;
**there is no `CHANGELOG.md`** — the
[GitHub Releases page](https://github.com/D2R-Reimagined/d2r-multi-export-tool/releases)
is the canonical changelog.

---

## License

By contributing, you agree that your contributions will be licensed
under the [GNU GPL-3.0-or-later](./LICENSE), the same license that
covers the rest of the project. Source files carry an
`// SPDX-License-Identifier: GPL-3.0-or-later` header; please keep
that header on any new `.cs` file you add.
