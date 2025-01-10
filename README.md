# SumECmd

Process Microsoft User Access Logs! Thanks to KPMG for the initial write up, found [here](https://advisory.kpmg.us/blog/2021/digital-forensics-incident-response.html).

## Command Line Interface

    SumECmd version 0.5.0.0

    Author: Eric Zimmerman (saericzimmerman@gmail.com)
    https://github.com/EricZimmerman/Sum

        d               Directory to recursively process, looking for SystemIdentity.mdb, Current.mdb, etc. Required.
        csv             Directory to save CSV formatted results to. Be sure to include the full path in double quotes

        wd              Generate CSV with day level details. Default is TRUE

        dt              The custom date/time format to use when displaying time stamps. Default is: yyyy-MM-dd HH:mm:ss.fffffff

        debug           Show debug information during processing
        trace           Show trace information during processing


    Examples: SumECmd.exe -d "C:\Temp\sum" --csv "C:\Temp\"

          Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes

## Documentation

[Windows User Access Logs (UAL)](https://svch0st.medium.com/windows-user-access-logs-ual-9580f1100635)

## Repairing the SUM Database

Until a proper guide can be made with test data, follow the guide for repairing the SRUM database found [here](https://github.com/EricZimmerman/Srum). Both the SUM and SRUM databases are ESE so the process will be identical. Additionally, a walkthrough on how to repair the SUM database was included in [this](https://svch0st.medium.com/windows-user-access-logs-ual-9580f1100635) blog post.

# Download Eric Zimmerman's Tools

All of Eric Zimmerman's tools can be downloaded [here](https://ericzimmerman.github.io/#!index.md). 

# Special Thanks

Open Source Development funding and support provided by the following contributors: [SANS Institute](http://sans.org/) and [SANS DFIR](http://dfir.sans.org/).
