using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using CsvHelper;
using Exceptionless;
using Fclp;
using NLog;
using NLog.Config;
using NLog.Targets;
using ServiceStack;
using SumData;

namespace SumECmd
{
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
        private static Logger _logger;

        private static FluentCommandLineParser<ApplicationArguments> _fluentCommandLineParser;

        private static readonly string BaseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private static DateTimeOffset _timestamp;


        public static bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void Main(string[] args)
        {
            ExceptionlessClient.Default.Startup("3DWn5we5zp92pJRvMetSThai8YZCuJQf7Lx3ssLl");

            SetupNLog();

            _logger = LogManager.GetCurrentClassLogger();


            _fluentCommandLineParser = new FluentCommandLineParser<ApplicationArguments>
            {
                IsCaseSensitive = false
            };


            _fluentCommandLineParser.Setup(arg => arg.Directory)
                .As('d')
                .WithDescription("Directory to recursively process, looking for SystemIdentity.mdb, Current.mdb, etc. Required.");

            _fluentCommandLineParser.Setup(arg => arg.CsvDirectory)
                .As("csv")
                .WithDescription(
                    "Directory to save CSV formatted results to. Be sure to include the full path in double quotes\r\n");

            
            _fluentCommandLineParser.Setup(arg => arg.WithDetail)
                .As("wd")
                .WithDescription("Generate CSV with day level details. Default is TRUE\r\n").SetDefault(true);

            _fluentCommandLineParser.Setup(arg => arg.DateTimeFormat)
                .As("dt")
                .WithDescription(
                    "The custom date/time format to use when displaying time stamps. Default is: yyyy-MM-dd HH:mm:ss.fffffff\r\n")
                .SetDefault("yyyy-MM-dd HH:mm:ss.fffffff");


            _fluentCommandLineParser.Setup(arg => arg.Debug)
                .As("debug")
                .WithDescription("Show debug information during processing").SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.Trace)
                .As("trace")
                .WithDescription("Show trace information during processing\r\n").SetDefault(false);


            var header =
                $"SumECmd version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/Sum";


            var footer = @"Examples: SumECmd.exe -d ""C:\Temp\sum"" --csv ""C:\Temp\"" " + "\r\n\t " +
                         "\r\n\t" +
                         "  Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes\r\n";

            _fluentCommandLineParser.SetupHelp("?", "help")
                .WithHeader(header)
                .Callback(text => _logger.Info(text + "\r\n" + footer));

            var result = _fluentCommandLineParser.Parse(args);

            if (result.HelpCalled)
            {
                return;
            }

            if (result.HasErrors)
            {
                _logger.Error("");
                _logger.Error(result.ErrorText);

                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                return;
            }

            if (_fluentCommandLineParser.Object.Directory.IsNullOrEmpty())
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("-d is required. Exiting\r\n");
                return;
            }


            if (_fluentCommandLineParser.Object.Directory.IsNullOrEmpty() == false &&
                !Directory.Exists(_fluentCommandLineParser.Object.Directory))
            {
                _logger.Warn($"Directory '{_fluentCommandLineParser.Object.Directory}' not found. Exiting");
                return;
            }

            if (_fluentCommandLineParser.Object.CsvDirectory.IsNullOrEmpty())
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("--csv is required. Exiting\r\n");
                return;
            }

            _logger.Info(header);
            _logger.Info("");
            _logger.Info($"Command line: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}\r\n");

            if (IsAdministrator() == false)
            {
                _logger.Fatal("Warning: Administrator privileges not found!\r\n");
            }

            if (_fluentCommandLineParser.Object.Debug)
            {
                LogManager.Configuration.LoggingRules.First().EnableLoggingForLevel(LogLevel.Debug);
            }

            if (_fluentCommandLineParser.Object.Trace)
            {
                LogManager.Configuration.LoggingRules.First().EnableLoggingForLevel(LogLevel.Trace);
            }

            LogManager.ReconfigExistingLoggers();

            var sw = new Stopwatch();
            sw.Start();

            _timestamp = DateTimeOffset.UtcNow;
            
            Sum sd = null;

            try
            {
                _logger.Info($"Processing '{_fluentCommandLineParser.Object.Directory}'...\r\n");
                sd = new Sum(_fluentCommandLineParser.Object.Directory);

                _logger.Warn("\r\nProcessing complete!\r\n");
                _logger.Warn("Summary info:");

                _logger.Info($"{"Role info count:".PadRight(30)} {sd.RoleInfos.Count:N0}");
                _logger.Info($"{"System Identity info count:".PadRight(30)} {sd.SystemIdentityInfos.Count:N0}");
                _logger.Info($"{"Chained DB info count:".PadRight(30)} {sd.ChainedDbs.Count:N0}");
                _logger.Info($"{"Processed DB info count:".PadRight(30)} {sd.ProcessedDatabases.Count:N0}");

                Console.WriteLine();
            }
            catch (FileNotFoundException fe)
            {
                _logger.Error(fe.Message);
                Console.WriteLine();
                Environment.Exit(0);
            }
            catch (Exception e)
            {
                _logger.Error($"Error processing file! Message: {e.Message}.\r\n\r\nThis almost always means the database is dirty and must be repaired. This can be verified by running 'esentutl.exe /mh <dbname>.mdb' and examining the 'State' property");
                Console.WriteLine();
                _logger.Info("If the database is dirty, **make a copy of your files**, ensure all files in the directory are not Read-only, open a PowerShell session as an admin, and repair by using the following commands (change directories to the location of <dbname>.mdb first):\r\n\r\n'esentutl.exe /r svc /i'\r\n'esentutl.exe /p <dbname>.mdb'\r\n\r\n");

                Console.WriteLine();

                Environment.Exit(0);
            }

            _logger.Warn("The following databases were processed:");
            foreach (var sdProcessedDatabase in sd.ProcessedDatabases)
            {
                _logger.Info($"\t{sdProcessedDatabase.FileName}");
            }

            _logger.Info("");

            _logger.Warn("Exporting data...");

            if (Directory.Exists(_fluentCommandLineParser.Object.CsvDirectory) == false)
            {
                Directory.CreateDirectory(_fluentCommandLineParser.Object.CsvDirectory);
            }


            ExportSystemIdentity(sd);
            ExportSystemRole(sd);
            ExportChainedDb(sd);
            
            ExportProcessedDetails(sd);


            sw.Stop();

            _logger.Info("");

            _logger.Error(
                $"Processing completed in {sw.Elapsed.TotalSeconds:N4} seconds\r\n");
        }

        private static CsvWriter GetCsvWriter(string tableName)
        {
            var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_DETAIL_{tableName}_Output.csv";
            
            var outFile = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, outName);

            _logger.Debug($"Setting up {tableName} output file: '{outFile}'");

            var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

            var csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

            return csvWriter;
        }

        private static void ExportProcessedDetails(Sum sd)
        {
            CsvWriter csvWriterClients = null;
            CsvWriter csvWriterClientsDetail = null;
            CsvWriter csvWriterDns = null; 
            CsvWriter csvWriterRoles = null; 
            CsvWriter csvWriterVm = null;

            foreach (var sdProcessedDatabase in sd.ProcessedDatabases)
            {
                _logger.Info($"Exporting Client info from '{Path.GetFileName(sdProcessedDatabase.FileName)}'...");
                foreach (var client in sdProcessedDatabase.Clients)
                {
                    foreach (var clientEntry in client.Value)
                    {
                        if (csvWriterClients == null)
                        {
                            csvWriterClients = GetCsvWriter("Clients");

                            var mapClient = csvWriterClients.Context.AutoMap<ClientEntry>();
                            mapClient.Map(t => t.InsertDate).Convert(t => $"{t.Value.InsertDate.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                            mapClient.Map(t => t.LastAccess).Convert(t => $"{t.Value.LastAccess.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                            csvWriterClients.Context.RegisterClassMap(mapClient);
                            csvWriterClients.WriteHeader<ClientEntry>();
                            csvWriterClients.NextRecord();
                        }

                        if (_fluentCommandLineParser.Object.WithDetail && csvWriterClientsDetail == null)
                        {
                            csvWriterClientsDetail = GetCsvWriter("ClientsDetailed");

                            var mapClient = csvWriterClients.Context.AutoMap<ClientEntryDayDetail>();
                            mapClient.Map(t => t.InsertDate).Convert(t => $"{t.Value.InsertDate.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                            mapClient.Map(t => t.LastAccess).Convert(t => $"{t.Value.LastAccess.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                            mapClient.Map(t => t.Date).Convert(t => $"{t.Value.Date.ToString("yyyy-MM-dd")}");
                            csvWriterClientsDetail.Context.RegisterClassMap(mapClient);
                            csvWriterClientsDetail.WriteHeader<ClientEntryDayDetail>();
                            csvWriterClientsDetail.NextRecord();
                        }

                        var roleDesc = sd.RoleInfos.SingleOrDefault(t => t.RoleGuid == client.Key);

                        clientEntry.RoleGuid = client.Key.ToString();
                        clientEntry.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);
                        
                        clientEntry.RoleDescription = roleDesc != null ? roleDesc.RoleName : "(Unknown Role Guid)";

                        if (_fluentCommandLineParser.Object.WithDetail)
                        {
                            foreach (var dayEntry in clientEntry.DayInfo)
                            {
                                var cedd = new ClientEntryDayDetail(clientEntry, dayEntry);
                                csvWriterClientsDetail.WriteRecord(cedd);
                                csvWriterClientsDetail.NextRecord();
                            }
                        }

                        csvWriterClients.WriteRecord(clientEntry);
                        csvWriterClients.NextRecord();
                    }
                }

                if (sdProcessedDatabase.DnsInfo.Any())
                {
                    _logger.Info($"Exporting DNS info from '{Path.GetFileName(sdProcessedDatabase.FileName)}'...");
                }
                foreach (var dnsEntry in sdProcessedDatabase.DnsInfo)
                {
                    if (csvWriterDns == null)
                    {
                        csvWriterDns = GetCsvWriter("DnsInfo");

                        var mapDns = csvWriterDns.Context.AutoMap<DnsEntry>();
                        mapDns.Map(t => t.LastSeen).Convert(t => $"{t.Value.LastSeen.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
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
                    _logger.Info($"Exporting Role access info from '{Path.GetFileName(sdProcessedDatabase.FileName)}'...");
                }
                foreach (var roleAccess in sdProcessedDatabase.RoleAccesses)
                {
                    if (csvWriterRoles == null)
                    {
                        csvWriterRoles = GetCsvWriter("RoleAccesses");

                        var mapRole = csvWriterRoles.Context.AutoMap<RoleAccessEntry>();
                        mapRole.Map(t => t.FirstSeen).Convert(t => $"{t.Value.FirstSeen.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                        mapRole.Map(t => t.LastSeen).Convert(t => $"{t.Value.LastSeen.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
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
                    _logger.Info($"Exporting Vm info from '{Path.GetFileName(sdProcessedDatabase.FileName)}'...");
                }
                foreach (var vm in sdProcessedDatabase.VmInfo)
                {
                    if (csvWriterVm == null)
                    {
                        csvWriterVm = GetCsvWriter("VmInfo");
                        var mapVm = csvWriterVm.Context.AutoMap<VmEntry>();
                        mapVm.Map(t => t.CreationTime).Convert(t => $"{t.Value.CreationTime.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                        mapVm.Map(t => t.LastSeenActive).Convert(t => $"{t.Value.LastSeenActive.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");
                        csvWriterVm.Context.RegisterClassMap(mapVm);
                        csvWriterVm.WriteHeader<VmEntry>();
                        csvWriterVm.NextRecord();
                    }
                    vm.SourceFile = Path.GetFileName(sdProcessedDatabase.FileName);
                    csvWriterVm.WriteRecord(vm);
                    csvWriterVm.NextRecord();
                }

                _logger.Info("-----------------------------------------------------------------------------------------------------");
            }

            csvWriterClients?.Flush();
            csvWriterDns?.Flush();
            csvWriterVm?.Flush();
            csvWriterRoles?.Flush();
            csvWriterClientsDetail?.Flush();

            _logger.Warn("\r\nExport totals");

            if (csvWriterClients != null)
            {
                _logger.Info($"Found {csvWriterClients.Row - 2:N0} Client entries");
            }

            if (csvWriterClientsDetail != null)
            {
                _logger.Info($"Found {csvWriterClientsDetail.Row - 2:N0} Client detail entries");
            }

            if (csvWriterDns != null)
            {
                _logger.Info($"Found {csvWriterDns.Row - 2:N0} DNS entries");
            }


            if (csvWriterRoles != null)
            {
                _logger.Info($"Found {csvWriterRoles.Row - 2:N0} Role entries");
            }
         

            if (csvWriterVm != null)
            {
                _logger.Info($"Found {csvWriterVm.Row - 2:N0} VM entries");
            }

          

           




        }

        private static void ExportChainedDb(Sum sd)
        {
            try
            {
                var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_SUMMARY_ChainedDbInfo_Output.csv";

                var outFile = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, outName);

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
                _logger.Error($"Error exporting 'SystemIdentInfo' data! Error: {e.Message}");
            }
        }

        private static void ExportSystemRole(Sum sd)
        {
            try
            {
                var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_SUMMARY_RoleInfos_Output.csv";

                var outFile = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, outName);

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
                _logger.Error($"Error exporting 'SystemIdentInfo' data! Error: {e.Message}");
            }
        }

        private static void ExportSystemIdentity(Sum sd)
        {
            try
            {
                var outName = $"{_timestamp:yyyyMMddHHmmss}_SumECmd_SUMMARY_SystemIdentInfo_Output.csv";

                var outFile = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, outName);

                var swCsv = new StreamWriter(outFile, false, Encoding.UTF8);

                var csvWriter = new CsvWriter(swCsv, CultureInfo.InvariantCulture);

                var foo = csvWriter.Context.AutoMap<SystemIdentInfo>();

                foo.Map(t => t.CreationTime).Convert(t => $"{t.Value.CreationTime.ToString(_fluentCommandLineParser.Object.DateTimeFormat)}");

                csvWriter.Context.RegisterClassMap(foo);
                csvWriter.WriteHeader<SystemIdentInfo>();
                csvWriter.NextRecord();

                csvWriter.WriteRecords(sd.SystemIdentityInfos);

                csvWriter.Flush();
                swCsv.Flush();
            }
            catch (Exception e)
            {
                _logger.Error($"Error exporting 'SystemIdentInfo' data! Error: {e.Message}");
            }
        }


        private static void SetupNLog()
        {
            if (File.Exists(Path.Combine(BaseDirectory, "Nlog.config")))
            {
                return;
            }

            var config = new LoggingConfiguration();
            var loglevel = LogLevel.Info;

            var layout = @"${message}";

            var consoleTarget = new ColoredConsoleTarget();

            config.AddTarget("console", consoleTarget);

            consoleTarget.Layout = layout;

            var rule1 = new LoggingRule("*", loglevel, consoleTarget);
            config.LoggingRules.Add(rule1);

            LogManager.Configuration = config;
        }
    }
}