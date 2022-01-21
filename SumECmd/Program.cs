using System;
using System.CommandLine;
using System.CommandLine.Help;
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
using Exceptionless.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ServiceStack;
using SumData;

namespace SumECmd;

public class ApplicationArguments
{
    public string Directory { get; set; }
    public string CsvDirectory { get; set; }

    public string DateTimeFormat { get; set; }

    public bool WithDetail { get; set; }
    public bool Debug { get; set; }
    public bool Trace { get; set; }
}


internal class Program
{
    
    private static readonly string BaseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    private static DateTimeOffset _timestamp;

    private static readonly string Header =
        $"SumECmd version {Assembly.GetExecutingAssembly().GetName().Version}" +
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

    private static async Task Main(string[] args)
    {
        ExceptionlessClient.Default.Startup("3DWn5we5zp92pJRvMetSThai8YZCuJQf7Lx3ssLl");

        _args = args;


        _rootCommand = new RootCommand
        {
            new Option<string>(
                "-d",
                "Directory to process, looking for SystemIdentity.mdb, Current.mdb, etc. Required.\r\n"),
                
            new Option<string>(
                "--csv",
                "Directory to save CSV formatted results to. Be sure to include the full path in double quotes\r\n"),
                
            new Option<bool>("--wd",
                getDefaultValue:()=>true,
                "Generate CSV with day level details. Default is TRUE"),
            
            new Option<string>(
                "--dt",
                getDefaultValue:()=>"yyyy-MM-dd HH:mm:ss",
                "The custom date/time format to use when displaying time stamps. See https://goo.gl/CNVq0k for options"),
            
            new Option<bool>(
                "--debug",
                getDefaultValue:()=>false,
                "Show debug information during processing"),
            
            new Option<bool>(
                "--trace",
                getDefaultValue:()=>false,
                "Show trace information during processing"),
                
        };
            
        _rootCommand.Description = Header + "\r\n\r\n" + Footer;

        _rootCommand.Handler = System.CommandLine.NamingConventionBinder.CommandHandler.Create<string,string,bool,string, bool,bool>(DoWork);
        
        await _rootCommand.InvokeAsync(args);
        
        Log.CloseAndFlush();
    }

    private static void DoWork(string d, string csv,bool wd, string dt, bool debug, bool trace)
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
            Log.Fatal("Non-Windows platforms not supported due to the need to load ESI specific Windows libraries! Exiting...");
            Console.WriteLine();
            Environment.Exit(0);
            return;
        }
     

        if (d.IsNullOrEmpty())
        {
            var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                    
            var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

            helpBld.Write(hc);
                    
            Log.Warning("-d is required. Exiting");
            Console.WriteLine();
            return;
        }


        if (d.IsNullOrEmpty() == false &&
            !Directory.Exists(d))
        {
            Log.Warning("Directory '{D}' not found. Exiting",d);
            Console.WriteLine();
            return;
        }

        if (csv.IsNullOrEmpty())
        {
            var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                    
            var hc = new HelpContext(helpBld,_rootCommand,Console.Out);

            helpBld.Write(hc);

            Log.Warning("--csv is required. Exiting");
            Console.WriteLine();
            return;
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
            
        Sum sd = null;

        try
        {
            Log.Information("Processing '{D}'...",d);
            Console.WriteLine();
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
            Log.Error(fe,"File not found: {Message}",fe.Message);
            Console.WriteLine();
            Environment.Exit(0);
        }
        catch (Exception e)
        {
            Log.Error(e,"Error processing file! Message: {Message}.\r\n\r\nThis almost always means the database is dirty and must be repaired. This can be verified by running 'esentutl.exe /mh <dbname>.mdb' and examining the 'State' property",e.Message);
            Console.WriteLine();
            Log.Information("If the database is dirty, **make a copy of your files**, ensure all files in the directory are not Read-only, open a PowerShell session as an admin, and repair by using the following commands (change directories to the location of <dbname>.mdb first):\r\n\r\n'esentutl.exe /r svc /i'\r\n'esentutl.exe /p <dbname>.mdb'\r\n\r\n");

            Console.WriteLine();

            Environment.Exit(0);
        }

        Log.Information("The following databases were processed:");
        foreach (var sdProcessedDatabase in sd.ProcessedDatabases)
        {
            Log.Information("\t{FileName}",sdProcessedDatabase.FileName);
        }

        Console.WriteLine();

        Log.Information("Exporting data...");

        if (Directory.Exists(csv) == false)
        {
            Directory.CreateDirectory(csv);
        }
        ExportSystemIdentity(sd,csv,dt);
        ExportSystemRole(sd,csv);
        ExportChainedDb(sd,csv);
            
        ExportProcessedDetails(sd,csv,dt,wd);

        sw.Stop();

        Console.WriteLine();

        Log.Information("Processing completed in {TotalSeconds:N4} seconds",sw.Elapsed.TotalSeconds);
        Console.WriteLine();
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

                        var mapClient = csvWriterClients.Context.AutoMap<ClientEntryDayDetail>();
                        mapClient.Map(t => t.InsertDate).Convert(t => $"{t.Value.InsertDate.ToString(dt)}");
                        mapClient.Map(t => t.LastAccess).Convert(t => $"{t.Value.LastAccess.ToString(dt)}");
                        mapClient.Map(t => t.Date).Convert(t => $"{t.Value.Date:yyyy-MM-dd}");
                        csvWriterClientsDetail.Context.RegisterClassMap(mapClient);
                        csvWriterClientsDetail.WriteHeader<ClientEntryDayDetail>();
                        csvWriterClientsDetail.NextRecord();
                    }

                    var roleDesc = sd.RoleInfos.SingleOrDefault(t => t.RoleGuid == client.Key);

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

    private static void ExportChainedDb(Sum sd, string csv)
    {
        try
        {
            var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_SUMMARY_ChainedDbInfo_Output.csv";

            var outFile = Path.Combine(csv, outName);

            var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

            var csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

            var foo = csvWriter.Context.AutoMap<ChainedDbInfo>();

            csvWriter.Context.RegisterClassMap(foo);
            csvWriter.WriteHeader<ChainedDbInfo>();
            csvWriter.NextRecord();

            csvWriter.WriteRecords(sd.ChainedDbs);

            csvWriter.Flush();
            swCsv.Flush();
        }
        catch (Exception e)
        {
            Log.Error(e,"Error exporting 'SystemIdentInfo' data! Error: {Message}",e.Message);
        }
    }

    private static void ExportSystemRole(Sum sd, string csv)
    {
        try
        {
            var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_SUMMARY_RoleInfos_Output.csv";

            var outFile = Path.Combine(csv, outName);

            var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

            var csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

            var foo = csvWriter.Context.AutoMap<RoleInfo>();

            csvWriter.Context.RegisterClassMap(foo);
            csvWriter.WriteHeader<RoleInfo>();
            csvWriter.NextRecord();

            csvWriter.WriteRecords(sd.RoleInfos);

            csvWriter.Flush();
            swCsv.Flush();
        }
        catch (Exception e)
        {
            Log.Error(e,"Error exporting 'SystemIdentInfo' data! Error: {Message}",e.Message);
        }
    }

    private static void ExportSystemIdentity(Sum sd, string csv, string dt)
    {
        try
        {
            var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_SUMMARY_SystemIdentInfo_Output.csv";

            var outFile = Path.Combine(csv, outName);

            var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

            var csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

            var foo = csvWriter.Context.AutoMap<SystemIdentInfo>();

            foo.Map(t => t.CreationTime).Convert(t => $"{t.Value.CreationTime.ToString(dt)}");

            csvWriter.Context.RegisterClassMap(foo);
            csvWriter.WriteHeader<SystemIdentInfo>();
            csvWriter.NextRecord();

            csvWriter.WriteRecords(sd.SystemIdentityInfos);

            csvWriter.Flush();
            swCsv.Flush();
        }
        catch (Exception e)
        {
            Log.Error(e,"Error exporting 'SystemIdentInfo' data! Error: {Message}",e.Message);
        }
    }


  
}