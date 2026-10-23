# SumECmd

Parses Microsoft User Access Logging (UAL) databases, the `C:\Windows\System32\LogFiles\Sum` folder on Windows Server:
the server's identity, its roles, and the clients, users and IP addresses that accessed each role, with their daily
access counts, DNS and virtual machine information. Results are exported to CSV.

| | |
|---|---|
| **Version** | 2026.10.0 |
| **Download** | [.NET 10](https://download.ericzimmermanstools.com/net/SumECmd.zip) (recommended) &middot; [.NET Framework 4.7.2](https://download.ericzimmermanstools.com/SumECmd.zip) |
| **All tools** | [ericzimmerman.github.io](https://ericzimmerman.github.io) or [Get-ZimmermanTools](https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip), which downloads them all and keeps them current |
| **Changes** | [ChangeLog](https://ericzimmerman.github.io/changelog/) |
| **Issues** | [EricZimmerman/Sum](https://github.com/EricZimmerman/Sum/issues) or [EricZimmerman/Issues](https://github.com/EricZimmerman/Issues/issues) |

## Requirements

- **.NET 10 build**: the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Run `SumECmd.exe` on
  Windows. SumECmd is Windows only: the databases are read with Windows' ESE engine (`esent.dll`).
- **.NET Framework build**: .NET Framework 4.7.2 or later (Windows only).

## Command line

```text
Usage:
  SumECmd [options]

Options:
  -d <d>          Directory to process, looking for SystemIdentity.mdb, Current.mdb, etc. Required.
  --csv <csv>     Directory to save CSV formatted results to. Be sure to include the full path in double quotes
  --wd            Generate CSV with day level details. Default is TRUE
  --dt <dt>       The custom date/time format to use when displaying time stamps. See https://goo.gl/CNVq0k for options [default: yyyy-MM-dd HH:mm:ss]
  --temp <temp>   Folder for the temporary copies of the UAL databases and their transaction logs, which are opened instead of the evidence and deleted when done. Default: a Temp folder next to SumECmd. A folder that isn't there is created, and removed again when done. If that folder can't be written to, SumECmd stops: give a writable folder with --temp
  --debug         Show debug information during processing
  --trace         Show trace information during processing
  -?, -h, --help  Show help and usage information
  --version       Show version information
```

Short options (a single letter) start with one dash, long options with two.

## Examples

```text
SumECmd.exe -d "C:\Temp\sum" --csv "C:\Temp\"
```

## Output

Seven CSVs, named `<timestamp>_SumECmd_SUMMARY_<name>_Output.csv` and `<timestamp>_SumECmd_DETAIL_<name>_Output.csv`:

- `SUMMARY_SystemIdentInfo`: one row per `SYSTEM_IDENTITY` entry: OS version, and (new in 2026.10.0, after the existing
  columns) host and domain name, manufacturer, model, serial numbers, last boot, time zone, processors and memory.
- `SUMMARY_RoleInfos`, `SUMMARY_ChainedDbInfo`: the role GUIDs and the yearly databases.
- `DETAIL_Clients`, `DETAIL_ClientsDetailed` (one row per day with accesses, with `--wd`), `DETAIL_DnsInfo`,
  `DETAIL_RoleAccesses`, `DETAIL_VmInfo`: from `Current.mdb` and each yearly (`{GUID}.mdb`) database, including yearly
  databases that `SystemIdentity.mdb` doesn't list.

`Current.mdb` does not store its year: the day columns are dated in the year of the latest access in it (earlier
versions used the year of the computer running SumECmd).

A database that can't be opened no longer loses the others, and a bad value no longer loses the rest of its table: the
problems are listed at the end.

## Dirty databases

The databases are never opened where they are: SumECmd copies them, with their transaction logs (`Svc*.log`,
`Svc.chk`, `*.jrs`), to a temporary folder, and works on the copies. Nothing in the `-d` folder changes, so it can be
read-only. The temporary folder is `--temp`, or by default a `Temp` folder next to SumECmd (never the user's `%TEMP%`);
the copies are deleted when done. If that folder can't be written to, SumECmd stops and asks for `--temp`.

UAL databases collected from a running server are usually in a dirty state. SumECmd handles this by itself, on the
copies:

1. The transaction logs, when they were collected, are replayed (what `esentutl.exe /r svc /i` does).
2. A database that is still dirty (its logs are missing) is repaired with `esentutl.exe /p`. What only the missing logs
   had is lost; SumECmd says when this was done.

There is no need to repair anything by hand any more. `esentutl.exe /mh <dbname>.mdb` shows a database's state.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. Databases that are damaged or missing from the `-d` folder are reported but are not a failure |
| `1` | The tool failed: invalid arguments, a missing `-d` folder, a database it can't read (access denied, locked), a `--temp` folder that can't be written to, an export that failed, or a system other than Windows |

## What's new in 2026.10

- The databases are read from copies in a temporary folder: nothing in the `-d` folder changes (it created `edb.chk`,
  `edb.log` and other files there).
- Dirty databases (as `Current.mdb` usually is) are handled automatically: logs are replayed into the copies, and a copy
  that is still dirty is repaired with `esentutl /p`. Repairing by hand is no longer needed.
- `Current.mdb`'s day counts are dated in the year of its data instead of the year of the computer running SumECmd.
- Day counts and OS build numbers over 32,767 are no longer negative.
- One database that can't be opened no longer loses the others, a bad value no longer loses the rest of its table, and
  yearly databases not listed in `SystemIdentity.mdb` are read too.
- `SystemIdentInfo` has the host and domain name, manufacturer, model, serial numbers, last boot, time zone, processors
  and memory, as new columns at the end.
- New `--temp`. Exit code 1 when the tool fails (it was always 0). Error reports include source file and line numbers.

The full list is in the [ChangeLog](https://ericzimmerman.github.io/changelog/).

## Building

```text
dotnet build Sum.slnx -c Release
```

Builds `net10.0` and `net472` (Windows only). The databases are read by the `SumData` library in this repository
([NuGet](https://www.nuget.org/packages/SumData)).

## Further reading

- [KPMG: initial write up on User Access Logging](https://advisory.kpmg.us/blog/2021/digital-forensics-incident-response.html)
- [Windows User Access Logs (UAL)](https://svch0st.medium.com/windows-user-access-logs-ual-9580f1100635)

## Special thanks

Open source development funding and support provided by the [SANS Institute](https://www.sans.org/) and
[SANS DFIR](https://www.sans.org/digital-forensics-incident-response/).

## License

[MIT](LICENSE), Copyright (c) Eric R. Zimmerman 2027.
