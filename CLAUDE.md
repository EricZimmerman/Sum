# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Part of Eric Zimmerman's DFIR tool suite: User Access Logging (UAL, `C:\Windows\System32\LogFiles\Sum`) on Windows Server.

- **`SumData`** (`netstandard2.0`, LangVersion 10): the library, on NuGet as `SumData` (no other repository uses it). ManagedEsent calls Windows' `esent.dll`, so it only runs on Windows.
  - `new Sum(directory)`: `SystemIdentity.mdb` (`SYSTEM_IDENTITY`, `ROLE_IDS`, `CHAINED_DATABASES`), `Current.mdb`, the chained (yearly, `{GUID}.mdb`) databases it lists, and any other `.mdb` in the folder (a yearly database it doesn't list, or no SystemIdentity.mdb). Throws only for a missing directory or when neither SystemIdentity.mdb nor Current.mdb is there.
  - **`EseWorkspace`** (internal): every database is read from a copy in `%TEMP%\SumData-<guid>`, never in place (ESE writes its checkpoint, logs and temp files in the folders it is given, replays logs into databases and attaches read-write; the old code did all of that in the evidence folder). The `Svc*` `.log`/`.chk`/`.jrs` files are copied too and replayed (`Recovery`, `AlternateDatabaseRecoveryDirectory` = the copies, `JET_bitReplayIgnoreMissingDB` = 0x4); if that fails, ESE runs without recovery. A copy that still won't attach is repaired once with `esentutl.exe /p` (there is no API for it), and that is reported in `Warnings`. The folder is deleted at the end.
  - Problems go to `ParseErrors` (a database, table or row that could not be read: the rest is kept), `FailedDatabases` (missing or could not be opened) and `Warnings` (logs not replayed, repaired copies). Every row is read on its own; column ids are looked up once per table (`GetColumnDictionary`); a missing column gives null/defaults. FILETIMEs out of range are `DateTimeOffset.MinValue` plus a `ParseErrors` entry.
  - Types as in the schema: `TotalAccesses` UInt32 (clamped to `int.MaxValue` with an error, as `ClientEntry.TotalAccesses` is an int), `Day1..Day366` and `OSBuildNumber` UInt16 (they were read as Int16: counts over 32,767 were negative).
  - **Years**: `CHAINED_DATABASES.Year` for the yearly databases; `Current.mdb` has none, so `ChainedDatabase.Year` comes from its data (the latest client access, role access or DNS time), else the newest chained year + 1, else the file's last write year. `YearSource` says which. (It was `DateTime.Now.Year`.)
  - `SystemIdentInfo`: the first four properties are the original CSV columns; the rest of `SYSTEM_IDENTITY` (host, domain, hardware, serials, time zone, ...) was added after them in 2026.10.0. Text is NUL padded in the database and trimmed.
- **`SumECmd`** (`net472;net10.0`): `-d` (required), `--csv` (required), `--wd` (day detail CSV), `--dt`. Seven CSVs named `<timestamp>_SumECmd_{SUMMARY|DETAIL}_<name>_Output.csv`; their column order must not change (Timeline Explorer's plugins match the headers), new columns go at the end. `Main` returns the exit code: 0, or 1 for invalid arguments, a database that is missing or failed, or an export that failed. Non-Windows exits with 1.

Input may be corrupt or hostile: keep what can be read and report the rest.

## Commands

The SDK is pinned in `global.json` (.NET 10). Tests use NUnit 5 + FluentAssertions 7 (pinned to `[7.2.2,8.0.0)`, since 8.x requires a commercial license). Windows only.

```bash
dotnet build -c Release                       # Sum.slnx
dotnet test -c Release                        # all tests (CI: windows-latest)
dotnet pack -c Release                        # SumData package
```

## Tests (`SumTest`, net10.0)

- `UalBuilder` creates UAL databases with ManagedEsent, with the column names and types of real ones (Server 2012 R2). A "dirty" database is ended with `JET_bitTermDirty` (0x8; `TermGrbit.Abrupt` still shut it down cleanly), leaving `Svc*.log` next to it.
- `SumTests`: every table, the day dates and years, unsigned values, bad values in a row, a folder that must not change (read-only files), replay from logs (no repair), repair without logs, a database that can't be opened, no SystemIdentity.mdb, no temp folders left. `WriteSyntheticFolders` (`[Explicit]`) writes the test folders to `SUM_SYNTHETIC` for comparing versions.
- `SampleTests`: the DFIR Artifact Museum's `Windows/SUM/Win2012R2/StolenSzechuan` (MIT), from `SUM_SAMPLES` or `Corpus/SUM` in a parent folder (`D:\Corpus\SUM` here); skipped without it. Expected values come from libesedb. Never copy sample or corpus files into the repo.
- `CliTests`: `SumECmd.dll` as a process: CSV headers and rows, exit codes.
- Validation of a change: old vs new SumECmd on copies of the sample and of the synthetic folders, CSVs compared row by row.

## Versioning and release

- **Library:** MinVer sets the version from the git tag (bare tags like `2026.10.0`); there is no `<Version>` in its csproj. To release: push to `master`, then `git tag <ver> && git push origin <ver> && gh release create <ver> --verify-tag --generate-notes`. `.github/workflows/publish.yml` (GuidMapping's, with `run_test` on `windows-latest`) pushes to NuGet with trusted publishing (`NUGET_USER` repo variable).
- **CLI:** `<Version>` in `SumECmd/SumECmd.csproj`, bumped by hand; the user builds, signs and releases it.
