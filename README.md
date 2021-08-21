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

** This is currently a copy of SrumECmd's guide, but the steps are identical for the SUM Database. Both the SUM and SRUM databases are ESE so the process will be identical. **

When you run SrumECmd, you will likely encounter an error message that states the file is dirty. 

    Command line: -d M:\Forensics\SrumECmdTest\tout -k --csv M:\Forensics\SrumECmdTest\mout\SystemActivity
    Found SRUM database file 'M:\Forensics\SrumECmdTest\tout\C\Windows\System32\SRU\SRUDB.dat'!
    Found SOFTWARE hive 'M:\Forensics\SrumECmdTest\tout\C\Windows\System32\config\software'!
    Processing 'M:\Forensics\SrumECmdTest\tout\C\Windows\System32\SRU\SRUDB.dat'...
    Error processing file! Message: Object reference not set to an instance of an object..
    This almost always means the database is dirty and must be repaired. This can be verified by running 'esentutl.exe /mh SRUDB.dat' and examining the 'State' property
    If the database is dirty, **make a copy of your files**, ensure all files in the directory are not Read-only, open a PowerShell session as an admin, and repair by using the following commands (change directories to the location of SRUDB.dat first):
    'esentutl.exe /r sru /i'
    'esentutl.exe /p SRUDB.dat'
    Executed 1 processor in 0.9046 seconds

    Total execution time: 6.5490 seconds
    
Follow these steps to repair the `SRUDB.dat` so you can run SrumECmd.exe again. First, follow the steps SrumECmd provides:
1. Make a copy of the files within the `.\SRU` directory

2. Ensure the `.\SRU` directory itself is not Read Only. This can be done by right clicking on the directory itself, Properties, and unchecking Read Only if it is checked:

![SRUFolderReadOnlyExample](Pictures/SRUFolderReadOnlyExample.gif)

3. Open a PowerShell session as an Administrator in the directory where your copied files reside

4. Execute this command within the PowerShell Admin session: 

    `esentutl.exe /r sru /i`

![SRUDBFirstRepairCommand](Pictures/SRUDBFirstRepairCommand.gif)

5. Execute this command within the PowerShell Admin session: 

    `esentutl.exe /p SRUDB.dat`
    
![SRUDBSecondRepairCommand](Pictures/SRUDBSecondRepairCommand.gif)

6. Try running SrumECmd again against the location where these repaired files reside:

![SrumECmdParsingSuccessExample](Pictures/SrumECmdParsingSuccessExample.png)

7. Examine output in Timeline Explorer!

![SrumECmdCSVOutput](Pictures/SrumECmdCSVOutput.jpg)

# Download Eric Zimmerman's Tools

All of Eric Zimmerman's tools can be downloaded [here](https://ericzimmerman.github.io/#!index.md). Use the [Get-ZimmermanTools](https://f001.backblazeb2.com/file/EricZimmermanTools/Get-ZimmermanTools.zip) PowerShell script to automate the download and updating of the EZ Tools suite. Additionally, you can automate each of these tools using [KAPE](https://www.kroll.com/en/services/cyber-risk/incident-response-litigation-support/kroll-artifact-parser-extractor-kape)!

# Special Thanks

Open Source Development funding and support provided by the following contributors: [SANS Institute](http://sans.org/) and [SANS DFIR](http://dfir.sans.org/).
