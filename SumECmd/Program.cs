using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using Exceptionless;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ServiceStack;
using SumData;

namespace SumECmd;

internal class Program
{
    private static DateTimeOffset _timestamp;

    private static readonly string Header =
        $"SumECmd version {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}" +
        "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
        "\r\nhttps://github.com/EricZimmerman/Sum";


    private static readonly string Footer = @"Examples: SumECmd.exe -d ""C:\Temp\sum"" --csv ""C:\Temp\"" " + "\r\n\t " +
                                            "\r\n\t" +
                                            "     Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes\r\n";

    private static RootCommand _rootCommand;
    private static string[] _args;


    private static bool IsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }



    private static async Task<int> Main(string[] args)
    {
        ExceptionlessClient.Default.Startup("3DWn5we5zp92pJRvMetSThai8YZCuJQf7Lx3ssLl");

        _args = args;

        var dOpt = new Option<string>("-d")
        {
            Description = "Directory to process, looking for SystemIdentity.mdb, Current.mdb, etc. in it and every folder under it (one set of CSVs, SourceDirectory says which folder a row is from). Required.\r\n"
        };

        var csvOpt = new Option<string>("--csv")
        {
            Description = "Directory to save CSV formatted results to. Be sure to include the full path in double quotes\r\n"
        };

        var wdOpt = new Option<bool>("--wd")
        {
            Description = "Generate CSV with day level details. Default is TRUE",
            DefaultValueFactory = _ => true
        };

        var dtOpt = new Option<string>("--dt")
        {
            Description = "The custom date/time format to use when displaying time stamps. See https://goo.gl/CNVq0k for options",
            DefaultValueFactory = _ => "yyyy-MM-dd HH:mm:ss"
        };

        var tempOpt = new Option<string>("--temp")
        {
            Description = TempFolder.Help
        };

        var debugOpt = new Option<bool>("--debug")
        {
            Description = "Show debug information during processing",
            DefaultValueFactory = _ => false
        };
        var traceOpt = new Option<bool>("--trace")
        {
            Description = "Show trace information during processing",
            DefaultValueFactory = _ => false
        };
        var coOpt = new Option<bool>("--co")
        {
            Description = "When true, save all console messages to <timestamp>_SumECmd_<version>_ConsoleLog.txt in the --csv directory. Use --co false to turn it off",
            DefaultValueFactory = _ => true
        };

        _rootCommand = new RootCommand
        {
           dOpt,
           csvOpt,
           wdOpt,
           dtOpt,
           tempOpt,
           debugOpt,
           traceOpt,
           coOpt

        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;

        _rootCommand.SetAction(result => DoWork(result.GetValue(dOpt), result.GetValue(csvOpt), result.GetValue(wdOpt),
            result.GetValue(dtOpt), result.GetValue(tempOpt), result.GetValue(debugOpt), result.GetValue(traceOpt), result.GetValue(coOpt)));

        var exitCode = await _rootCommand.Parse(args).InvokeAsync();

        Log.CloseAndFlush();

        return exitCode;
    }

    /// <returns>
    ///     0 when SumECmd did its job, including databases that are missing or damaged (they are reported); 1 for invalid
    ///     arguments, a missing -d folder, files it could not read (access denied, locked, IO errors) or an export that failed
    /// </returns>
    private static int DoWork(string d, string csv,bool wd, string dt, string temp, bool debug, bool trace, bool co)
    {
        var levelSwitch = new LoggingLevelSwitch();

        var template = "{Message:lj}{NewLine}{Exception}";

        if (debug)
        {
            levelSwitch.MinimumLevel = LogEventLevel.Debug;
            template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        }

        if (trace)
        {
            levelSwitch.MinimumLevel = LogEventLevel.Verbose;
            template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        }

        var conf = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: template)
            .MinimumLevel.ControlledBy(levelSwitch);

        //the time in the output names, and in the console log's
        _timestamp = DateTimeOffset.UtcNow;

        var consoleLog = co ? ConsoleLog.GetPath(_timestamp, csv) : null;
        if (consoleLog != null)
        {
            conf.WriteTo.File(consoleLog, outputTemplate: ConsoleLog.Template);
        }

        Log.Logger = conf.CreateLogger();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine();
            Log.Fatal("Non-Windows platforms not supported due to the need to load ESE specific Windows libraries! Exiting...");
            Console.WriteLine();
            return 1;
        }

        if (d.IsNullOrEmpty())
        {
            var aaa = new CustomHelpAction(new HelpAction());
            aaa.Invoke(_rootCommand.Parse("-d is required. Exiting"));

            Console.WriteLine();
            return 1;
        }

        if (!Directory.Exists(d))
        {
            Log.Warning("Directory '{D}' not found. Exiting",d);
            Console.WriteLine();
            return 1;
        }

        if (csv.IsNullOrEmpty())
        {
            var aaa = new CustomHelpAction(new HelpAction());
            aaa.Invoke(_rootCommand.Parse("--csv is required. Exiting"));

            Console.WriteLine();
            return 1;
        }

        Log.Information("{Header}",Header);
        Console.WriteLine();
        Log.Information("Command line: {Args}",string.Join(" ", _args));
        Console.WriteLine();

        if (consoleLog != null)
        {
            Log.Information("Console log: {Path}",consoleLog);
            Console.WriteLine();
        }

        if (IsAdministrator() == false)
        {
            Log.Warning("Warning: Administrator privileges not found!");
            Console.WriteLine();
        }

        var sw = new Stopwatch();
        sw.Start();

        //Issues #265: every folder under -d with a SystemIdentity.mdb or Current.mdb, each read on its own
        var unreadable = new List<string>();
        var folders = Sum.FindDatabaseDirectories(d, unreadable);
        var failed = false;

        foreach (var folder in unreadable)
        {
            Log.Error("Unable to read folder {Folder}",folder);
            failed = true;
        }

        if (folders.Count == 0)
        {
            //nothing to do
            Log.Error("Neither '{SystemIdentity}' nor '{Current}' was found in or under '{D}'!","SystemIdentity.mdb","Current.mdb",d);
            Console.WriteLine();
            return failed ? 1 : 0;
        }

        if (folders.Count > 1)
        {
            Log.Information("Found User Access Logging databases in {Count:N0} folders under '{D}'",folders.Count,d);
            Console.WriteLine();
        }

        //the databases are read from copies, made in --temp or Temp next to SumECmd (never the user's %TEMP%)
        var tempFolder = TempFolder.Prepare(temp, out var tempCreated, out var tempError);
        if (tempFolder == null)
        {
            Log.Error("The folder for the temporary copies of the databases can't be written to ({Error}). Give one with --temp. Exiting", tempError);
            Console.WriteLine();
            return 1;
        }

        var sums = new List<Sum>();

        try
        {
            foreach (var folder in folders)
            {
                Log.Information("Processing '{D}'...",folder);
                Console.WriteLine();
                //the databases are read from copies in the temporary folder: nothing in -d is changed
                try
                {
                    sums.Add(new Sum(folder, tempFolder));
                }
                catch (FileNotFoundException fe)
                {
                    //gone since it was found
                    Log.Error("{Message}",fe.Message);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    //files or folders that could not be read (access denied, locked, IO errors) are a failure; the message names the path, a stack trace adds nothing
                    Log.Error("Error processing '{D}'! Message: {Message}",folder,e.Message);
                    failed = true;
                }
                catch (Exception e)
                {
                    //damaged databases are not a failure of the tool
                    Log.Error(e,"Error processing '{D}'! Message: {Message}",folder,e.Message);
                }

                Console.WriteLine();
            }
        }
        finally
        {
            TempFolder.RemoveIfEmpty(tempFolder, tempCreated);
        }

        if (sums.Count == 0)
        {
            return failed ? 1 : 0;
        }

        Log.Information("Processing complete!");
        Console.WriteLine();
        Log.Information("Summary info:");

        Log.Information("{Role} {RoleInfosCount:N0}","Role info count:".PadRight(30),sums.Sum(t => t.RoleInfos.Count));
        Log.Information("{SystemIdentity} {SystemIdentityInfosCount:N0}","System Identity info count:".PadRight(30),sums.Sum(t => t.SystemIdentityInfos.Count));
        Log.Information("{ChainedDb} {ChainedDbsCount:N0}","Chained DB info count:".PadRight(30),sums.Sum(t => t.ChainedDbs.Count));
        Log.Information("{ProcessedDb} {ProcessedDatabasesCount:N0}","Processed DB info count:".PadRight(30),sums.Sum(t => t.ProcessedDatabases.Count));

        Console.WriteLine();

        if (sums.Any(sd => sd.FailedDatabases.Count > 0 && sd.ParseErrors.Any(t => t.Contains("could not be opened"))))
        {
            Log.Information("A database that still can't be opened may be damaged beyond what 'esentutl.exe /p' (run on a copy) can repair. 'esentutl.exe /mh <dbname>.mdb' shows its state");
            Console.WriteLine();
        }

        if (sums.Any(t => t.ProcessedDatabases.Count > 0))
        {
            Log.Information("The following databases were processed:");
            foreach (var sdProcessedDatabase in sums.SelectMany(t => t.ProcessedDatabases))
            {
                Log.Information("\t{FileName} (year {Year}, from {YearSource})",sdProcessedDatabase.FileName,sdProcessedDatabase.Year,sdProcessedDatabase.YearSource);
            }

            Console.WriteLine();
        }

        Log.Information("Exporting data...");

        var exportFailed = false;

        try
        {
            if (Directory.Exists(csv) == false)
            {
                Directory.CreateDirectory(csv);
            }

            //one set of CSVs for all the folders; SourceDirectory (the last column) says where each row is from
            exportFailed |= !ExportSystemIdentity(sums,csv,dt);
            exportFailed |= !ExportSystemRole(sums,csv);
            exportFailed |= !ExportChainedDb(sums,csv);

            ExportProcessedDetails(sums,csv,dt,wd);
        }
        catch (Exception e)
        {
            Log.Error(e,"Error exporting data to '{Csv}': {Message}",csv,e.Message);
            exportFailed = true;
        }

        sw.Stop();

        var parseErrors = sums.SelectMany(t => t.ParseErrors).ToList();
        if (parseErrors.Count > 0)
        {
            Console.WriteLine();
            Log.Warning("{Count:N0} problem(s) found while reading the databases (everything else was exported):",parseErrors.Count);
            foreach (var parseError in parseErrors.Take(50))
            {
                Log.Warning("\t{ParseError}",parseError);
            }

            if (parseErrors.Count > 50)
            {
                Log.Warning("\t... and {Count:N0} more (use --debug to see them all as they happen)",parseErrors.Count - 50);
            }
        }

        Console.WriteLine();

        Log.Information("Processing completed in {TotalSeconds:N4} seconds",sw.Elapsed.TotalSeconds);
        Console.WriteLine();

        //databases that are missing or could not be opened (damaged) are reported above, but are not a failure of SumECmd
        return exportFailed || failed ? 1 : 0;
    }

    private static CsvWriter GetCsvWriter<T>(string tableName, string csv, CsvRecordWriter<T> recordWriter)
    {
        var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_DETAIL_{tableName}_Output.csv";

        var outFile = Path.Combine(csv, outName);

        Log.Debug("Setting up {TableName} output file: '{OutFile}'",tableName,outFile);

        //the record writer of this CSV's type, which WriteHeader and WriteRecord use
        CsvRecordWriter.Register<T>(_ => recordWriter);

        //the same output as CsvHelper 33, UTF-8 with a BOM
        return CsvWriter.ToFile(outFile, CsvOutput.Options with { WriteByteOrderMark = true });
    }

    private static void ExportProcessedDetails(List<Sum> sums,string csv, string dt, bool wd)
    {
        CsvWriter csvWriterClients = null;
        CsvWriter csvWriterClientsDetail = null;
        CsvWriter csvWriterDns = null;
        CsvWriter csvWriterRoles = null;
        CsvWriter csvWriterVm = null;

        //(none of them was ever closed)
        try
        {
            foreach (var (sd, sdProcessedDatabase) in sums.SelectMany(t => t.ProcessedDatabases.Select(db => (t, db))))
{
                Log.Information("Exporting Client info from '{FileName}'...",Path.GetFileName(sdProcessedDatabase.FileName));
                foreach (var client in sdProcessedDatabase.Clients)
                {
                    foreach (var clientEntry in client.Value)
                    {
                        if (csvWriterClients == null)
                        {
                            csvWriterClients = GetCsvWriter("Clients",csv,SumCsv.Client(dt));
                            csvWriterClients.WriteHeader<ClientEntry>();
                        }

                        if (wd && csvWriterClientsDetail == null)
                        {
                            csvWriterClientsDetail = GetCsvWriter("ClientsDetailed",csv,SumCsv.ClientDetail(dt));
                            csvWriterClientsDetail.WriteHeader<ClientEntryDayDetail>();
                        }

                        //(SingleOrDefault threw for a role listed twice)
                        var roleDesc = sd.RoleInfos.FirstOrDefault(t => t.RoleGuid == client.Key);

                        clientEntry.RoleGuid = client.Key.ToString();
                        clientEntry.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);

                        clientEntry.RoleDescription = roleDesc != null ? roleDesc.RoleName : "(Unknown Role Guid)";

                        if (wd)
                        {
                            foreach (var dayEntry in clientEntry.DayInfo)
                            {
                                var clientEntryDayDetail = new ClientEntryDayDetail(clientEntry, dayEntry);
                                csvWriterClientsDetail.WriteRecord(clientEntryDayDetail);
                            }
                        }

                        csvWriterClients.WriteRecord(clientEntry);
                    }
                }

                if (sdProcessedDatabase.DnsInfo.Any())
                {
                    Log.Information("Exporting DNS info from '{FileName}'...",Path.GetFileName(sdProcessedDatabase.FileName));
                }
                foreach (var dnsEntry in sdProcessedDatabase.DnsInfo)
                {
                    if (csvWriterDns == null)
                    {
                        csvWriterDns = GetCsvWriter("DnsInfo",csv,SumCsv.Dns(dt));
                        csvWriterDns.WriteHeader<DnsEntry>();
                    }

                    dnsEntry.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);
                    csvWriterDns.WriteRecord(dnsEntry);
                }

                if (sdProcessedDatabase.RoleAccesses.Any())
                {
                    Log.Information("Exporting Role access info from '{FileName}'...",Path.GetFileName(sdProcessedDatabase.FileName));
                }
                foreach (var roleAccess in sdProcessedDatabase.RoleAccesses)
                {
                    if (csvWriterRoles == null)
                    {
                        csvWriterRoles = GetCsvWriter("RoleAccesses",csv,SumCsv.RoleAccess(dt));
                        csvWriterRoles.WriteHeader<RoleAccessEntry>();
                    }
                    roleAccess.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);

                    var roleDesc = sd.RoleInfos.FirstOrDefault(t => t.RoleGuid == roleAccess.RoleGuid);

                    roleAccess.RoleDescription = roleDesc != null ? roleDesc.RoleName : "(Unknown Role Guid)";

                    csvWriterRoles.WriteRecord(roleAccess);
                }

                if (sdProcessedDatabase.VmInfo.Any())
                {
                    Log.Information("Exporting Vm info from '{FileName}'...",Path.GetFileName(sdProcessedDatabase.FileName));
                }
                foreach (var vm in sdProcessedDatabase.VmInfo)
                {
                    if (csvWriterVm == null)
                    {
                        csvWriterVm = GetCsvWriter("VmInfo",csv,SumCsv.Vm(dt));
                        csvWriterVm.WriteHeader<VmEntry>();
                    }
                    vm.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);
                    csvWriterVm.WriteRecord(vm);
                }

                Log.Information("-----------------------------------------------------------------------------------------------------");
            }

            csvWriterClients?.Flush();
            csvWriterDns?.Flush();
            csvWriterVm?.Flush();
            csvWriterRoles?.Flush();
            csvWriterClientsDetail?.Flush();

            Console.WriteLine();
            Log.Information("Export totals");

            if (csvWriterClients != null)
            {
                Log.Information("Found {Rows:N0} Client entries",csvWriterClients.RecordCount - 1);
            }

            if (csvWriterClientsDetail != null)
            {
                Log.Information("Found {Rows:N0} Client detail entries",csvWriterClientsDetail.RecordCount - 1);
            }

            if (csvWriterDns != null)
            {
                Log.Information("Found {Rows:N0} DNS entries",csvWriterDns.RecordCount - 1);
            }


            if (csvWriterRoles != null)
            {
                Log.Information("Found {Rows:N0} Role entries",csvWriterRoles.RecordCount - 1);
            }


            if (csvWriterVm != null)
            {
                Log.Information("Found {Rows:N0} VM entries",csvWriterVm.RecordCount - 1);
            }
        }
        finally
        {
            csvWriterClients?.Dispose();
            csvWriterClientsDetail?.Dispose();
            csvWriterDns?.Dispose();
            csvWriterRoles?.Dispose();
            csvWriterVm?.Dispose();
        }
    }

    /// <summary>
    ///     One summary CSV (written even when it has no rows, as before)
    /// </summary>
    private static bool ExportSummary<T>(IEnumerable<T> records, string csv, string name, CsvRecordWriter<T> recordWriter)
    {
        try
        {
            var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_SUMMARY_{name}_Output.csv";

            var outFile = Path.Combine(csv, outName);

            //the same output as CsvHelper 33, UTF-8 with a BOM
            using var csvWriter = CsvWriter.ToFile(outFile, CsvOutput.Options with { WriteByteOrderMark = true });

            csvWriter.WriteRecords(records, recordWriter);

            return true;
        }
        catch (Exception e)
        {
            //(the messages for the chained database and role CSVs said SystemIdentInfo)
            Log.Error(e,"Error exporting '{Name}' data! Error: {Message}",name,e.Message);
            return false;
        }
    }

    private static bool ExportChainedDb(List<Sum> sums, string csv)
    {
        return ExportSummary(sums.SelectMany(t => t.ChainedDbs), csv, "ChainedDbInfo", SumCsv.ChainedDbInfo());
    }

    private static bool ExportSystemRole(List<Sum> sums, string csv)
    {
        return ExportSummary(sums.SelectMany(t => t.RoleInfos), csv, "RoleInfos", SumCsv.RoleInfo());
    }

    private static bool ExportSystemIdentity(List<Sum> sums, string csv, string dt)
    {
        return ExportSummary(sums.SelectMany(t => t.SystemIdentityInfos), csv, "SystemIdentInfo", SumCsv.SystemIdentInfo(dt));
    }

    private class CustomHelpAction : SynchronousCommandLineAction
    {
        private readonly HelpAction _defaultHelp;

        public CustomHelpAction(HelpAction action)
        {
            _defaultHelp = action;
        }

        public override int Invoke(ParseResult parseResult)
        {
            var result = _defaultHelp.Invoke(parseResult);

            Log.Warning("{Msg}", string.Join(" ",parseResult.Tokens));

            return result;
        }
    }

}
