# Layout marker

`SourceRoots` requires a mod root to contain both `global/excel` and
`local/lng/strings`. The property-range snapshot test reads only the excel
tables, so this directory carries no translations; git cannot track an empty
directory.
