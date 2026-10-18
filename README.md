# SumECmd

Process Microsoft User Access Logs (UAL, the `SUM` folder on Windows Server)! Thanks to KPMG for the initial write up, found [here](https://advisory.kpmg.us/blog/2021/digital-forensics-incident-response.html).

## Command Line Interface

    SumECmd version 2026.10.0

    Author: Eric Zimmerman (saericzimmerman@gmail.com)
    https://github.com/EricZimmerman/Sum

        -d              Directory to process, looking for SystemIdentity.mdb, Current.mdb, etc. Required.
        --csv           Directory to save CSV formatted results to. Be sure to include the full path in double quotes

        --wd            Generate CSV with day level details. Default is TRUE

        --dt            The custom date/time format to use when displaying time stamps. Default is: yyyy-MM-dd HH:mm:ss

        --debug         Show debug information during processing
        --trace         Show trace information during processing


    Examples: SumECmd.exe -d "C:\Temp\sum" --csv "C:\Temp\"

          Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes

Exit codes: 0 on success; 1 for invalid arguments, a database that is missing or can't be opened, or an export that failed. Everything that could be read is exported either way.

Windows only (the databases are read with Windows' ESE engine, `esent.dll`).

## Output

- `SUMMARY_SystemIdentInfo`: one row per `SYSTEM_IDENTITY` entry: OS version, and (new in 2026.10.0, after the existing columns) host and domain name, manufacturer, model, serial numbers, last boot, time zone, processors and memory.
- `SUMMARY_RoleInfos`, `SUMMARY_ChainedDbInfo`: the role GUIDs and the yearly databases.
- `DETAIL_Clients`, `DETAIL_ClientsDetailed` (one row per day with accesses), `DETAIL_DnsInfo`, `DETAIL_RoleAccesses`, `DETAIL_VmInfo`: from `Current.mdb` and each yearly (`{GUID}.mdb`) database.

`Current.mdb` does not store its year: the day columns are dated in the year of the latest access in it (earlier versions used the year of the computer running SumECmd).

## Dirty databases

The databases are never opened where they are: SumECmd copies them, with their transaction logs (`Svc*.log`, `Svc.chk`, `*.jrs`), to a temporary folder, and works on the copies. Nothing in the `-d` folder changes, so it can be read-only.

UAL databases collected from a running server are usually in a dirty state. SumECmd handles this by itself, on the copies:

1. The transaction logs, when they were collected, are replayed (what `esentutl.exe /r svc /i` does).
2. A database that is still dirty (its logs are missing) is repaired with `esentutl.exe /p`. What only the missing logs had is lost; SumECmd says when this was done.

There is no need to repair anything by hand any more. `esentutl.exe /mh <dbname>.mdb` shows a database's state.

## Documentation

[Windows User Access Logs (UAL)](https://svch0st.medium.com/windows-user-access-logs-ual-9580f1100635)

# Download Eric Zimmerman's Tools

All of Eric Zimmerman's tools can be downloaded [here](https://ericzimmerman.github.io/#!index.md). 

# Special Thanks

Open Source Development funding and support provided by the following contributors: [SANS Institute](http://sans.org/) and [SANS DFIR](http://dfir.sans.org/).
