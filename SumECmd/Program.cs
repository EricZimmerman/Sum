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
            Description = "Directory to process, looking for SystemIdentity.mdb, Current.mdb, etc. Required.\r\n"
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

        _rootCommand = new RootCommand
        {
           dOpt,
           csvOpt,
           wdOpt,
           dtOpt,
           debugOpt,
           traceOpt

        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;

        _rootCommand.SetAction(result => DoWork(result.GetValue(dOpt), result.GetValue(csvOpt), result.GetValue(wdOpt),
            result.GetValue(dtOpt), result.GetValue(debugOpt), result.GetValue(traceOpt)));

        var exitCode = await _rootCommand.Parse(args).InvokeAsync();

        Log.CloseAndFlush();

        return exitCode;
    }

    /// <returns>0 on success; 1 for invalid arguments, a database that is missing or could not be read, or an export that failed</returns>
    private static int DoWork(string d, string csv,bool wd, string dt, bool debug, bool trace)
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

        if (IsAdministrator() == false)
        {
            Log.Warning("Warning: Administrator privileges not found!");
            Console.WriteLine();
        }

        var sw = new Stopwatch();
        sw.Start();

        _timestamp = DateTimeOffset.UtcNow;

        Sum sd;

        try
        {
            Log.Information("Processing '{D}'...",d);
            Console.WriteLine();
            //the databases are read from copies in a temporary folder: nothing in -d is changed
            sd = new Sum(d);
            Console.WriteLine();
            Log.Information("Processing complete!");
            Console.WriteLine();
            Log.Information("Summary info:");

            Log.Information("{Role} {RoleInfosCount:N0}","Role info count:".PadRight(30),sd.RoleInfos.Count);
            Log.Information("{SystemIdentity} {SystemIdentityInfosCount:N0}","System Identity info count:".PadRight(30),sd.SystemIdentityInfos.Count);
            Log.Information("{ChainedDb} {ChainedDbsCount:N0}","Chained DB info count:".PadRight(30),sd.ChainedDbs.Count);
            Log.Information("{ProcessedDb} {ProcessedDatabasesCount:N0}","Processed DB info count:".PadRight(30),sd.ProcessedDatabases.Count);

            Console.WriteLine();
        }
        catch (FileNotFoundException fe)
        {
            Log.Error("{Message}",fe.Message);
            Console.WriteLine();
            return 1;
        }
        catch (Exception e)
        {
            Log.Error(e,"Error processing '{D}'! Message: {Message}",d,e.Message);
            Console.WriteLine();
            return 1;
        }

        if (sd.FailedDatabases.Count > 0 && sd.ParseErrors.Any(t => t.Contains("could not be opened")))
        {
            Log.Information("A database that still can't be opened may be damaged beyond what 'esentutl.exe /p' (run on a copy) can repair. 'esentutl.exe /mh <dbname>.mdb' shows its state");
            Console.WriteLine();
        }

        if (sd.ProcessedDatabases.Count > 0)
        {
            Log.Information("The following databases were processed:");
            foreach (var sdProcessedDatabase in sd.ProcessedDatabases)
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

            exportFailed |= !ExportSystemIdentity(sd,csv,dt);
            exportFailed |= !ExportSystemRole(sd,csv);
            exportFailed |= !ExportChainedDb(sd,csv);

            ExportProcessedDetails(sd,csv,dt,wd);
        }
        catch (Exception e)
        {
            Log.Error(e,"Error exporting data to '{Csv}': {Message}",csv,e.Message);
            exportFailed = true;
        }

        sw.Stop();

        if (sd.ParseErrors.Count > 0)
        {
            Console.WriteLine();
            Log.Warning("{Count:N0} problem(s) found while reading the databases (everything else was exported):",sd.ParseErrors.Count);
            foreach (var parseError in sd.ParseErrors.Take(50))
            {
                Log.Warning("\t{ParseError}",parseError);
            }

            if (sd.ParseErrors.Count > 50)
            {
                Log.Warning("\t... and {Count:N0} more (use --debug to see them all as they happen)",sd.ParseErrors.Count - 50);
            }
        }

        Console.WriteLine();

        Log.Information("Processing completed in {TotalSeconds:N4} seconds",sw.Elapsed.TotalSeconds);
        Console.WriteLine();

        return sd.FailedDatabases.Count > 0 || exportFailed ? 1 : 0;
    }

    private static CsvWriter GetCsvWriter(string tableName, string csv)
    {
        var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_DETAIL_{tableName}_Output.csv";

        var outFile = Path.Combine(csv, outName);

        Log.Debug("Setting up {TableName} output file: '{OutFile}'",tableName,outFile);

        var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

        var csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

        return csvWriter;
    }

    private static void ExportProcessedDetails(Sum sd,string csv, string dt, bool wd)
    {
        CsvWriter csvWriterClients = null;
        CsvWriter csvWriterClientsDetail = null;
        CsvWriter csvWriterDns = null;
        CsvWriter csvWriterRoles = null;
        CsvWriter csvWriterVm = null;

        //(none of them was ever closed)
        try
        {
            foreach (var sdProcessedDatabase in sd.ProcessedDatabases)
            {
                Log.Information("Exporting Client info from '{FileName}'...",Path.GetFileName(sdProcessedDatabase.FileName));
                foreach (var client in sdProcessedDatabase.Clients)
                {
                    foreach (var clientEntry in client.Value)
                    {
                        if (csvWriterClients == null)
                        {
                            csvWriterClients = GetCsvWriter("Clients",csv);

                            var mapClient = csvWriterClients.Context.AutoMap<ClientEntry>();
                            mapClient.Map(t => t.InsertDate).Convert(t => $"{t.Value.InsertDate.ToString(dt)}");
                            mapClient.Map(t => t.LastAccess).Convert(t => $"{t.Value.LastAccess.ToString(dt)}");
                            csvWriterClients.Context.RegisterClassMap(mapClient);
                            csvWriterClients.WriteHeader<ClientEntry>();
                            csvWriterClients.NextRecord();
                        }

                        if (wd && csvWriterClientsDetail == null)
                        {
                            csvWriterClientsDetail = GetCsvWriter("ClientsDetailed",csv);

                            var mapClient = csvWriterClientsDetail.Context.AutoMap<ClientEntryDayDetail>();
                            mapClient.Map(t => t.InsertDate).Convert(t => $"{t.Value.InsertDate.ToString(dt)}");
                            mapClient.Map(t => t.LastAccess).Convert(t => $"{t.Value.LastAccess.ToString(dt)}");
                            mapClient.Map(t => t.Date).Convert(t => $"{t.Value.Date:yyyy-MM-dd}");
                            csvWriterClientsDetail.Context.RegisterClassMap(mapClient);
                            csvWriterClientsDetail.WriteHeader<ClientEntryDayDetail>();
                            csvWriterClientsDetail.NextRecord();
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
                                csvWriterClientsDetail.NextRecord();
                            }
                        }

                        csvWriterClients.WriteRecord(clientEntry);
                        csvWriterClients.NextRecord();
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
                        csvWriterDns = GetCsvWriter("DnsInfo",csv);

                        var mapDns = csvWriterDns.Context.AutoMap<DnsEntry>();
                        mapDns.Map(t => t.LastSeen).Convert(t => $"{t.Value.LastSeen.ToString(dt)}");
                        csvWriterDns.Context.RegisterClassMap(mapDns);
                        csvWriterDns.WriteHeader<DnsEntry>();
                        csvWriterDns.NextRecord();
                    }

                    dnsEntry.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);
                    csvWriterDns.WriteRecord(dnsEntry);
                    csvWriterDns.NextRecord();
                }

                if (sdProcessedDatabase.RoleAccesses.Any())
                {
                    Log.Information("Exporting Role access info from '{FileName}'...",Path.GetFileName(sdProcessedDatabase.FileName));
                }
                foreach (var roleAccess in sdProcessedDatabase.RoleAccesses)
                {
                    if (csvWriterRoles == null)
                    {
                        csvWriterRoles = GetCsvWriter("RoleAccesses",csv);

                        var mapRole = csvWriterRoles.Context.AutoMap<RoleAccessEntry>();
                        mapRole.Map(t => t.FirstSeen).Convert(t => $"{t.Value.FirstSeen.ToString(dt)}");
                        mapRole.Map(t => t.LastSeen).Convert(t => $"{t.Value.LastSeen.ToString(dt)}");
                        csvWriterRoles.Context.RegisterClassMap(mapRole);
                        csvWriterRoles.WriteHeader<RoleAccessEntry>();
                        csvWriterRoles.NextRecord();
                    }
                    roleAccess.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);

                    var roleDesc = sd.RoleInfos.FirstOrDefault(t => t.RoleGuid == roleAccess.RoleGuid);

                    roleAccess.RoleDescription = roleDesc != null ? roleDesc.RoleName : "(Unknown Role Guid)";

                    csvWriterRoles.WriteRecord(roleAccess);
                    csvWriterRoles.NextRecord();
                }

                if (sdProcessedDatabase.VmInfo.Any())
                {
                    Log.Information("Exporting Vm info from '{FileName}'...",Path.GetFileName(sdProcessedDatabase.FileName));
                }
                foreach (var vm in sdProcessedDatabase.VmInfo)
                {
                    if (csvWriterVm == null)
                    {
                        csvWriterVm = GetCsvWriter("VmInfo",csv);
                        var mapVm = csvWriterVm.Context.AutoMap<VmEntry>();
                        mapVm.Map(t => t.CreationTime).Convert(t => $"{t.Value.CreationTime.ToString(dt)}");
                        mapVm.Map(t => t.LastSeenActive).Convert(t => $"{t.Value.LastSeenActive.ToString(dt)}");
                        csvWriterVm.Context.RegisterClassMap(mapVm);
                        csvWriterVm.WriteHeader<VmEntry>();
                        csvWriterVm.NextRecord();
                    }
                    vm.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);
                    csvWriterVm.WriteRecord(vm);
                    csvWriterVm.NextRecord();
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
                Log.Information("Found {Rows:N0} Client entries",csvWriterClients.Row - 2);
            }

            if (csvWriterClientsDetail != null)
            {
                Log.Information("Found {Rows:N0} Client detail entries",csvWriterClientsDetail.Row - 2);
            }

            if (csvWriterDns != null)
            {
                Log.Information("Found {Rows:N0} DNS entries",csvWriterDns.Row - 2);
            }


            if (csvWriterRoles != null)
            {
                Log.Information("Found {Rows:N0} Role entries",csvWriterRoles.Row - 2);
            }


            if (csvWriterVm != null)
            {
                Log.Information("Found {Rows:N0} VM entries",csvWriterVm.Row - 2);
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
    private static bool ExportSummary<T>(IEnumerable<T> records, string csv, string name, Action<CsvHelper.Configuration.ClassMap<T>> configure = null)
    {
        try
        {
            var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_SUMMARY_{name}_Output.csv";

            var outFile = Path.Combine(csv, outName);

            using var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

            using var csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

            var map = csvWriter.Context.AutoMap<T>();
            configure?.Invoke(map);

            csvWriter.Context.RegisterClassMap(map);
            csvWriter.WriteHeader<T>();
            csvWriter.NextRecord();

            csvWriter.WriteRecords(records);

            return true;
        }
        catch (Exception e)
        {
            //(the messages for the chained database and role CSVs said SystemIdentInfo)
            Log.Error(e,"Error exporting '{Name}' data! Error: {Message}",name,e.Message);
            return false;
        }
    }

    private static bool ExportChainedDb(Sum sd, string csv)
    {
        return ExportSummary(sd.ChainedDbs, csv, "ChainedDbInfo");
    }

    private static bool ExportSystemRole(Sum sd, string csv)
    {
        return ExportSummary(sd.RoleInfos, csv, "RoleInfos");
    }

    private static bool ExportSystemIdentity(Sum sd, string csv, string dt)
    {
        return ExportSummary(sd.SystemIdentityInfos, csv, "SystemIdentInfo",
            map => map.Map(t => t.CreationTime).Convert(t => $"{t.Value.CreationTime.ToString(dt)}"));
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
