## Summary

<!-- One or two sentences describing what this PR does and why. -->

## Scope

- [ ] At least one commit on this PR follows Conventional Commits
      (`feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `perf:`, `test:`,
      `build:`, `ci:`, `revert:`, `style:`). The CI gate enforces this.
- [ ] Touched code follows `AGENTS.md`: 4-space indent, file-scoped
      namespaces, `_camelCase` private fields, no hardcoded D2R codes /
      magic numbers (use `D2RMultiExport.Lib\Config\*.json`).
- [ ] No new player-facing string is written as raw English into
      `<out>/keyed/*.json` (use `KeyedLine` + `synthetic-strings.json`).
      The three documented exceptions in `AGENTS.md` are unchanged.

## Verification

- [ ] `dotnet build .\D2RMultiExport.sln -c Release` is clean.
- [ ] If the import / translation / export pipeline was touched, the
      smoke export from `docs\TESTING.md` was run against a real
      D2R Reimagined mod tree and printed
      `Export completed successfully.` with no new errors in
      `extras\import-report.txt`. (Skip only for pure docs / comments
      changes — and say so in the summary.)

## Notes for reviewers

<!-- Anything non-obvious: design tradeoffs, follow-ups intentionally
     deferred, breaking changes for the website consumer, etc. -->
