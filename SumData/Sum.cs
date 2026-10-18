using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Isam.Esent.Interop;
using Serilog;


namespace SumData
{
    public class Sum
    {
        //largest FILETIME a DateTimeOffset can hold
        private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

        private const string SystemIdentityName = "SystemIdentity.mdb";
        private const string CurrentName = "Current.mdb";

        /// <summary>
        ///     Reads the User Access Logging databases in <paramref name="directory" />: SystemIdentity.mdb, Current.mdb and
        ///     the chained (yearly) databases. They are read from copies in a temporary folder (see
        ///     <see cref="EseWorkspace" />), so nothing in <paramref name="directory" /> changes. A database or a row that
        ///     can't be read is reported in <see cref="ParseErrors" /> and the rest is kept
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">The directory does not exist</exception>
        /// <exception cref="FileNotFoundException">Neither SystemIdentity.mdb nor Current.mdb is in the directory</exception>
        public Sum(string directory)
        {
            //https://docs.microsoft.com/en-us/previous-versions/windows/desktop/ual/msftual-hyperv
            //SystemIdentity.mdb: SYSTEM_IDENTITY (os info), ROLE_IDS (role guids), CHAINED_DATABASES (a database per year)
            //Current.mdb: this year's CLIENTS, DNS, ROLE_ACCESS and VIRTUALMACHINES; the chained databases have the same tables

            SystemIdentityInfos = new List<SystemIdentInfo>();
            RoleInfos = new List<RoleInfo>();
            ChainedDbs = new List<ChainedDbInfo>();
            ProcessedDatabases = new List<ChainedDatabase>();
            ParseErrors = new List<string>();
            Warnings = new List<string>();
            FailedDatabases = new List<string>();

            if (Directory.Exists(directory) == false)
            {
                throw new DirectoryNotFoundException($"Directory '{directory}' not found!");
            }

            var mdbFiles = Directory.GetFiles(directory, "*.mdb");

            var sysIdent = mdbFiles.FirstOrDefault(f => Path.GetFileName(f).Equals(SystemIdentityName, StringComparison.OrdinalIgnoreCase));
            var currentDb = mdbFiles.FirstOrDefault(f => Path.GetFileName(f).Equals(CurrentName, StringComparison.OrdinalIgnoreCase));

            if (sysIdent == null && currentDb == null)
            {
                throw new FileNotFoundException($"Neither '{SystemIdentityName}' nor '{CurrentName}' was found in '{directory}'!");
            }

            using var ws = EseWorkspace.Create(directory, mdbFiles, "Svc", AddWarning);

            if (sysIdent != null)
            {
                Log.Information("Found '{SysIdent}'. Processing...", sysIdent);
                ProcessSystemIdentityDatabase(ws, sysIdent);
            }
            else
            {
                AddError($"'{SystemIdentityName}' not found in '{directory}': no role names, system identity or chained database list");
                FailedDatabases.Add(Path.Combine(directory, SystemIdentityName));
            }

            if (currentDb != null)
            {
                Log.Information("Found '{CurrentDb}'. Processing...", currentDb);
                //its year is not stored: it comes from the data (it was the year of the machine running this)
                ProcessDatabase(ws, currentDb, null);
            }
            else
            {
                AddError($"'{CurrentName}' not found in '{directory}'");
                FailedDatabases.Add(Path.Combine(directory, CurrentName));
            }

            foreach (var chainedDbInfo in ChainedDbs)
            {
                var chainFile = mdbFiles.FirstOrDefault(f => Path.GetFileName(f).Equals(chainedDbInfo.FileName, StringComparison.OrdinalIgnoreCase));

                if (chainFile == null)
                {
                    Log.Information("Chained database '{ChainFile}' for year {Year} does not exist! Skipping...", Path.Combine(directory, chainedDbInfo.FileName), chainedDbInfo.Year);
                    continue;
                }

                Log.Information("Found '{ChainFile}' for year {Year}. Processing...", chainFile, chainedDbInfo.Year);
                ProcessDatabase(ws, chainFile, chainedDbInfo.Year);
            }

            //yearly databases that CHAINED_DATABASES doesn't list (or there was no SystemIdentity.mdb)
            foreach (var other in mdbFiles)
            {
                var name = Path.GetFileName(other);
                if (name.Equals(SystemIdentityName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(CurrentName, StringComparison.OrdinalIgnoreCase) ||
                    ChainedDbs.Any(c => c.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Log.Information("Found '{Other}', not listed in CHAINED_DATABASES. Processing...", other);
                ProcessDatabase(ws, other, null);
            }
        }

        public string GetRoleDescFromGuid(Guid roleGuid)
        {
            var r = RoleInfos.FirstOrDefault(t => t.RoleGuid == roleGuid);

            return r?.RoleName;
        }

        public List<SystemIdentInfo> SystemIdentityInfos { get; }
        public List<RoleInfo> RoleInfos { get; }
        public List<ChainedDbInfo> ChainedDbs { get; }

        public List<ChainedDatabase> ProcessedDatabases { get; }

        /// <summary>
        ///     Databases, tables and rows that could not be read (everything else was kept)
        /// </summary>
        public List<string> ParseErrors { get; }

        /// <summary>
        ///     What was done to read the databases: logs that could not be replayed, copies repaired with esentutl /p, years
        ///     taken from the data
        /// </summary>
        public List<string> Warnings { get; }

        /// <summary>
        ///     Databases that are missing or could not be opened at all
        /// </summary>
        public List<string> FailedDatabases { get; }

        private void AddError(string message)
        {
            ParseErrors.Add(message);
            Log.Warning("{Message}", message);
        }

        private void AddWarning(string message)
        {
            Warnings.Add(message);
            Log.Warning("{Message}", message);
        }

        /// <summary>
        ///     Opens the copy of <paramref name="dbFile" />, or reports why it can't be
        /// </summary>
        private bool TryOpen(EseWorkspace ws, string dbFile, out string copy, out JET_DBID dbid)
        {
            copy = Path.Combine(ws.WorkDirectory, Path.GetFileName(dbFile));
            dbid = JET_DBID.Nil;

            try
            {
                dbid = ws.OpenDatabase(copy);
                return true;
            }
            catch (Exception e)
            {
                AddError($"'{dbFile}' could not be opened: {e.Message}");
                FailedDatabases.Add(dbFile);
                return false;
            }
        }

        private void ProcessDatabase(EseWorkspace ws, string dbFile, int? year)
        {
            if (!TryOpen(ws, dbFile, out var copy, out var dbid))
            {
                return;
            }

            var dbName = Path.GetFileName(dbFile);
            var session = ws.Session;

            var chainedDb = new ChainedDatabase(dbFile);

            //day counts get their dates once the year is known
            var days = new List<(ClientEntry Client, int Day, int Count)>();

            Log.Debug("Getting Clients info");
            ReadTable(session, dbid, dbName, "CLIENTS", (table, cols) =>
            {
                var authUserName = Str(session, table, cols, "AuthenticatedUserName");
                var clientName = Str(session, table, cols, "ClientName");

                var insertDate = FileTime(session, table, cols, "InsertDate", dbName, "CLIENTS");
                var lastAccess = FileTime(session, table, cols, "LastAccess", dbName, "CLIENTS");

                var roleGuid = Col(cols, "RoleGuid") is { } rg ? Api.RetrieveColumnAsGuid(session, table, rg) ?? Guid.Empty : Guid.Empty;
                var tenantId = Col(cols, "TenantId") is { } ti ? Api.RetrieveColumnAsGuid(session, table, ti) ?? Guid.Empty : Guid.Empty;
                var address = Col(cols, "Address") is { } ad ? Api.RetrieveColumn(session, table, ad) : null;

                //unsigned 32 bits
                var totalAccessRaw = Col(cols, "TotalAccesses") is { } ta ? Api.RetrieveColumnAsUInt32(session, table, ta) ?? 0 : 0;
                if (totalAccessRaw > int.MaxValue)
                {
                    AddError($"{dbName} CLIENTS: TotalAccesses {totalAccessRaw} is larger than {int.MaxValue}; {int.MaxValue} is used");
                    totalAccessRaw = int.MaxValue;
                }

                if (chainedDb.Clients.ContainsKey(roleGuid) == false)
                {
                    chainedDb.Clients.Add(roleGuid, new List<ClientEntry>());
                }

                var ce = new ClientEntry(ConvertBytesToIpAddress(address), authUserName, clientName, insertDate, lastAccess, tenantId, (int) totalAccessRaw);

                for (var i = 1; i <= 366; i++)
                {
                    if (!cols.TryGetValue($"Day{i}", out var dayCol))
                    {
                        continue;
                    }

                    //unsigned 16 bits (counts over 32,767 were negative)
                    var dayVal = Api.RetrieveColumnAsUInt16(session, table, dayCol);

                    if (dayVal.HasValue)
                    {
                        days.Add((ce, i, dayVal.Value));
                    }
                }

                chainedDb.Clients[roleGuid].Add(ce);

                Log.Verbose("Added client info: {Ce}", ce);
            });

            Log.Debug("Getting DNS info");
            ReadTable(session, dbid, dbName, "DNS", (table, cols) =>
            {
                var address = Str(session, table, cols, "Address");
                var hostName = Str(session, table, cols, "HostName");

                var lastSeen = FileTime(session, table, cols, "LastSeen", dbName, "DNS");

                var dns = new DnsEntry(lastSeen, address, hostName);

                chainedDb.DnsInfo.Add(dns);
                Log.Verbose("Added DNS info: {Dns}", dns);
            });

            Log.Debug("Getting Role Access info");
            ReadTable(session, dbid, dbName, "ROLE_ACCESS", (table, cols) =>
            {
                var firstSeen = FileTime(session, table, cols, "FirstSeen", dbName, "ROLE_ACCESS");
                var lastSeen = FileTime(session, table, cols, "LastSeen", dbName, "ROLE_ACCESS");

                var roleGuid = Col(cols, "RoleGuid") is { } rg ? Api.RetrieveColumnAsGuid(session, table, rg) ?? Guid.Empty : Guid.Empty;

                var re = new RoleAccessEntry(firstSeen, lastSeen, roleGuid);

                chainedDb.RoleAccesses.Add(re);
                Log.Verbose("Added role access info: {Re}", re);
            });

            Log.Debug("Getting Virtual Machines info");
            ReadTable(session, dbid, dbName, "VIRTUALMACHINES", (table, cols) =>
            {
                var serialNumber = Str(session, table, cols, "SerialNumber");

                var creationTime = FileTime(session, table, cols, "CreationTime", dbName, "VIRTUALMACHINES");
                var lastSeenActive = FileTime(session, table, cols, "LastSeenActive", dbName, "VIRTUALMACHINES");

                var biosGuid = Col(cols, "BIOSGuid") is { } bg ? Api.RetrieveColumnAsGuid(session, table, bg) ?? Guid.Empty : Guid.Empty;
                var vmGuid = Col(cols, "VmGuid") is { } vg ? Api.RetrieveColumnAsGuid(session, table, vg) ?? Guid.Empty : Guid.Empty;

                var vm = new VmEntry(biosGuid, vmGuid, creationTime, lastSeenActive, serialNumber);

                chainedDb.VmInfo.Add(vm);

                Log.Verbose("Added VM info: {Vm}", vm);
            });

            ws.CloseDatabase(copy, dbid);

            if (year == null)
            {
                year = YearFromData(chainedDb, out var source);
                chainedDb.YearSource = source;

                if (days.Count > 0)
                {
                    Log.Information("{DbName}: day counts are dated in {Year} ({Source})", dbName, year, source);
                }
            }
            else
            {
                chainedDb.YearSource = "CHAINED_DATABASES";
            }

            chainedDb.Year = year.Value;

            var jan1 = new DateTimeOffset(year.Value, 1, 1, 0, 0, 0, TimeSpan.Zero);
            foreach (var (client, day, count) in days)
            {
                client.DayInfo.Add(new DayEntry(day, jan1.AddDays(day - 1), count));
            }

            //all done, so add it to list
            ProcessedDatabases.Add(chainedDb);
        }

        /// <summary>
        ///     The year of a database that doesn't say: the latest year in its times, else the year after the newest chained
        ///     database, else the year the database file was last written
        /// </summary>
        private int YearFromData(ChainedDatabase db, out string source)
        {
            var times = db.Clients.Values.SelectMany(c => c).Select(c => c.LastAccess)
                .Concat(db.RoleAccesses.Select(r => r.LastSeen))
                .Concat(db.DnsInfo.Select(d => d.LastSeen))
                .Where(t => t > DateTimeOffset.MinValue && t.Year > 1601)
                .ToList();

            if (times.Count > 0)
            {
                source = "the year of the latest access in it";
                return times.Max().Year;
            }

            if (ChainedDbs.Count > 0)
            {
                source = "the year after the newest chained database";
                return ChainedDbs.Max(c => c.Year) + 1;
            }

            source = "the year the database file was last written";
            return File.GetLastWriteTimeUtc(db.FileName).Year;
        }

        /// <summary>
        ///     Every row of a table, each one on its own: a row that fails is reported and the next one is read
        /// </summary>
        private void ReadTable(Session session, JET_DBID dbid, string dbName, string tableName,
            Action<Table, IDictionary<string, JET_COLUMNID>> readRow)
        {
            Table table;
            try
            {
                table = new Table(session, dbid, tableName, OpenTableGrbit.ReadOnly);
            }
            catch (EsentObjectNotFoundException)
            {
                Log.Debug("{DbName} has no table {TableName}", dbName, tableName);
                return;
            }
            catch (Exception e)
            {
                AddError($"{dbName} {tableName}: the table could not be opened: {e.Message}");
                return;
            }

            using (table)
            {
                var row = 0;
                try
                {
                    //looked up once per table (they were looked up for every column of every row)
                    var cols = Api.GetColumnDictionary(session, table);

                    Api.JetSetTableSequential(session, table, SetTableSequentialGrbit.None);

                    Api.MoveBeforeFirst(session, table);

                    while (Api.TryMoveNext(session, table))
                    {
                        row += 1;
                        try
                        {
                            readRow(table, cols);
                        }
                        catch (Exception e)
                        {
                            AddError($"{dbName} {tableName} row {row}: {e.Message}");
                        }
                    }

                    Api.JetResetTableSequential(session, table, ResetTableSequentialGrbit.None);
                }
                catch (Exception e)
                {
                    AddError($"{dbName} {tableName}: reading stopped after {row:N0} rows: {e.Message}");
                }
            }
        }

        private static JET_COLUMNID? Col(IDictionary<string, JET_COLUMNID> cols, string name)
        {
            return cols.TryGetValue(name, out var c) ? c : null;
        }

        /// <summary>
        ///     A text column without its NUL padding; null when the column or its value is missing
        /// </summary>
        private static string Str(Session session, Table table, IDictionary<string, JET_COLUMNID> cols, string name)
        {
            return Col(cols, name) is { } c ? Api.RetrieveColumnAsString(session, table, c)?.Trim('\0') : null;
        }

        /// <summary>
        ///     A FILETIME as UTC; <see cref="DateTimeOffset.MinValue" /> when missing, or (reported) out of range
        /// </summary>
        private DateTimeOffset FileTime(Session session, Table table, IDictionary<string, JET_COLUMNID> cols, string name, string dbName, string tableName)
        {
            if (!(Col(cols, name) is { } c))
            {
                return DateTimeOffset.MinValue;
            }

            var raw = Api.RetrieveColumnAsInt64(session, table, c);

            if (raw == null)
            {
                return DateTimeOffset.MinValue;
            }

            if (raw.Value < 0 || raw.Value > MaxFileTime)
            {
                AddError($"{dbName} {tableName}: {name} 0x{raw.Value:X} is not a valid time");
                return DateTimeOffset.MinValue;
            }

            return new DateTimeOffset(DateTime.FromFileTimeUtc(raw.Value));
        }

        private static string ConvertBytesToIpAddress(byte[] rawBytes)
        {
            if (rawBytes == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var counter = 1;

            string ip;

            if (rawBytes.Length > 10)
            {
                foreach (var rawByte in rawBytes)
                {
                    sb.Append(rawByte.ToString("X2"));
                    if (counter % 2 == 0)
                    {
                        sb.Append(":");
                    }

                    counter += 1;
                }
                ip = sb.ToString().Trim(':');

            }
            else
            {
                //v4
                foreach (var rawByte in rawBytes)
                {
                    sb.Append($"{rawByte}.");
                }

                ip = sb.ToString().Trim('.');
            }


            return ip;
        }

        private void ProcessSystemIdentityDatabase(EseWorkspace ws, string sysIdent)
        {
            if (!TryOpen(ws, sysIdent, out var copy, out var dbid))
            {
                return;
            }

            var dbName = Path.GetFileName(sysIdent);
            var session = ws.Session;

            Log.Debug("Getting System identity info");
            ReadTable(session, dbid, dbName, "SYSTEM_IDENTITY", (table, cols) =>
            {
                var creationTime = FileTime(session, table, cols, "CreationTime", dbName, "SYSTEM_IDENTITY");

                var osMajor = Col(cols, "OSMajor") is { } mj ? Api.RetrieveColumnAsByte(session, table, mj) ?? 0 : 0;
                var osMinor = Col(cols, "OSMinor") is { } mn ? Api.RetrieveColumnAsByte(session, table, mn) ?? 0 : 0;
                var osBuildNum = Col(cols, "OSBuildNumber") is { } bn ? Api.RetrieveColumnAsUInt16(session, table, bn) ?? 0 : 0;

                var si = new SystemIdentInfo(creationTime, osMajor, osMinor, osBuildNum)
                {
                    SystemDnsHostName = Str(session, table, cols, "SystemDNSHostName"),
                    SystemDomainName = Str(session, table, cols, "SystemDomainName"),
                    SystemManufacturer = Str(session, table, cols, "SystemManufacturer"),
                    SystemProductName = Str(session, table, cols, "SystemProductName"),
                    SystemSerialNumber = Str(session, table, cols, "SystemSerialNumber"),
                    SystemSmbiosUuid = Str(session, table, cols, "SystemSMBIOSUUID"),
                    OsSerialNumber = Str(session, table, cols, "OSSerialNumber"),
                    OsLastBootUpTime = Str(session, table, cols, "OSLastBootUpTime"),
                    OsCountryCode = Str(session, table, cols, "OSCountryCode"),
                    //minutes from UTC (signed)
                    OsCurrentTimeZone = Col(cols, "OSCurrentTimeZone") is { } tz ? Api.RetrieveColumnAsInt16(session, table, tz) : null,
                    OsDaylightInEffect = Col(cols, "OSDaylightInEffect") is { } dl ? Api.RetrieveColumnAsBoolean(session, table, dl) : null,
                    OsProductType = Col(cols, "OSProductType") is { } pt ? Api.RetrieveColumnAsByte(session, table, pt) : null,
                    OsSuiteMask = Col(cols, "OSSuiteMask") is { } sm ? Api.RetrieveColumnAsUInt16(session, table, sm) : null,
                    OsPlatformId = Col(cols, "OSPlatformId") is { } pl ? Api.RetrieveColumnAsUInt16(session, table, pl) : null,
                    ServicePackMajor = Col(cols, "ServicePackMajor") is { } spj ? Api.RetrieveColumnAsByte(session, table, spj) : null,
                    ServicePackMinor = Col(cols, "ServicePackMinor") is { } spn ? Api.RetrieveColumnAsByte(session, table, spn) : null,
                    PhysicalProcessorCount = Col(cols, "PhysicalProcessorCount") is { } pp ? Api.RetrieveColumnAsByte(session, table, pp) : null,
                    CoresPerPhysicalProcessor = Col(cols, "CoresPerPhysicalProcessor") is { } cp ? Api.RetrieveColumnAsByte(session, table, cp) : null,
                    LogicalProcessorsPerPhysicalProcessor = Col(cols, "LogicalProcessorsPerPhysicalProcessor") is { } lp ? Api.RetrieveColumnAsByte(session, table, lp) : null,
                    MaximumMemory = Col(cols, "MaximumMemory") is { } mm ? Api.RetrieveColumnAsInt64(session, table, mm) : null
                };

                SystemIdentityInfos.Add(si);

                Log.Verbose("Added system identity info: {Si}", si);
            });

            Log.Debug("Getting Role Id info");
            ReadTable(session, dbid, dbName, "ROLE_IDS", (table, cols) =>
            {
                var roleGuid = Col(cols, "RoleGuid") is { } rg ? Api.RetrieveColumnAsGuid(session, table, rg) ?? Guid.Empty : Guid.Empty;
                var prodName = Str(session, table, cols, "ProductName");
                var roleName = Str(session, table, cols, "RoleName");

                var ri = new RoleInfo(roleGuid, prodName, roleName);

                RoleInfos.Add(ri);

                Log.Verbose("Added role info: {Ri}", ri);
            });

            Log.Debug("Getting Chained database info");
            ReadTable(session, dbid, dbName, "CHAINED_DATABASES", (table, cols) =>
            {
                var year = Col(cols, "Year") is { } y ? Api.RetrieveColumnAsUInt16(session, table, y) : null;
                var fileName = Str(session, table, cols, "FileName");

                if (year == null || string.IsNullOrEmpty(fileName))
                {
                    throw new InvalidDataException($"no {(year == null ? "Year" : "FileName")}");
                }

                var cd = new ChainedDbInfo(year.Value, fileName);

                ChainedDbs.Add(cd);

                Log.Verbose("Added chained db info: {Cd}", cd);
            });

            ws.CloseDatabase(copy, dbid);
        }

        /// <summary>
        ///     Writes the tables and columns of an ESE database to the console (read from a copy)
        /// </summary>
        public static void DumpTableInfo(string fileName)
        {
            if (File.Exists(fileName) == false)
            {
                throw new FileNotFoundException($"'{fileName}' does not exist!");
            }

            var full = Path.GetFullPath(fileName);
            using var ws = EseWorkspace.Create(Path.GetDirectoryName(full), new[] {full}, "Svc", m => Log.Warning("{Message}", m));
            var copy = Path.Combine(ws.WorkDirectory, Path.GetFileName(full));
            var dbid = ws.OpenDatabase(copy);

            foreach (var table in Api.GetTableNames(ws.Session, dbid))
            {
                Console.WriteLine($"TABLE: {table}");

                foreach (var column in Api.GetTableColumns(ws.Session, dbid, table))
                {
                    Console.WriteLine("\t{0}: {1}", column.Name, column.Coltyp);
                }

                Console.WriteLine("------------------------------------");
            }

            ws.CloseDatabase(copy, dbid);
        }
    }


    public class ChainedDatabase
    {
        public ChainedDatabase(string fileName)
        {
            FileName = fileName;

            DnsInfo = new List<DnsEntry>();
            RoleAccesses = new List<RoleAccessEntry>();
            Clients = new Dictionary<Guid, List<ClientEntry>>();
            VmInfo = new List<VmEntry>();
        }

        public string FileName {get;set;}

        /// <summary>
        ///     The year of the day counts (Day1 is January 1st)
        /// </summary>
        public int Year { get; internal set; }

        /// <summary>
        ///     Where <see cref="Year" /> comes from: CHAINED_DATABASES, or the data for Current.mdb
        /// </summary>
        public string YearSource { get; internal set; }

        public List<DnsEntry> DnsInfo { get; }
        public List<RoleAccessEntry> RoleAccesses { get; }
        public List<VmEntry> VmInfo { get; }

        public Dictionary<Guid,List<ClientEntry>> Clients { get; }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"File Name {FileName}");
            sb.AppendLine();

            if (RoleAccesses.Any())
            {
                sb.AppendLine($"** ROLE ACCESSES **");
                foreach (var ra in RoleAccesses)
                {
                    sb.AppendLine(ra.ToString());
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("!!!! No Role Accesses present !!!!\r\n");
            }

            if (VmInfo.Any())
            {
                sb.AppendLine($"** VIRTUAL MACHINES **");
                foreach (var vm in VmInfo)
                {
                    sb.AppendLine(vm.ToString());
                }

                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("!!!! No VM Info present !!!!\r\n");
            }

            if (DnsInfo.Any())
            {
                sb.AppendLine($"** DNS INFO **");
                foreach (var dns in DnsInfo)
                {
                    sb.AppendLine(dns.ToString());

                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("!!!! No DNS Info present !!!!\r\n");
            }

            if (Clients.Any())
            {
                sb.AppendLine($"** CLIENTS **");
                foreach (var client in Clients)
                {
                    sb.AppendLine($"Role Guid: {client.Key}");

                    foreach (var clientEntry in client.Value)
                    {
                        sb.AppendLine(clientEntry.ToString());
                    }
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("!!!! No Client Info present !!!!\r\n");
            }

            return sb.ToString();
        }
    }

    public class ClientEntryDayDetail:ClientEntry
    {
        public ClientEntryDayDetail(ClientEntry clientEntry,DayEntry dayEntry) : base(clientEntry.IpAddress, clientEntry.AuthenticatedUserName, clientEntry.ClientName, clientEntry.InsertDate, clientEntry.LastAccess, clientEntry.TenantId, clientEntry.TotalAccesses)
        {
            DayNumber = dayEntry.DayNumber;
            Date = dayEntry.Date;
            Count = dayEntry.Count;
            SourceFile = clientEntry.SourceFile;
            RoleDescription = clientEntry.RoleDescription;
            RoleGuid = clientEntry.RoleGuid;
        }

        public DateTimeOffset Date {get;}
        public int Count { get; }

        public int DayNumber { get;  }



    }

    public class ClientEntry
    {
        public ClientEntry(string ipAddress, string authenticatedUserName, string clientName, DateTimeOffset insertDate, DateTimeOffset lastAccess, Guid tenantId, int totalAccesses)
        {
            IpAddress = ipAddress;
            AuthenticatedUserName = authenticatedUserName;
            ClientName = clientName;
            InsertDate = insertDate;
            LastAccess = lastAccess;
            TenantId = tenantId;
            TotalAccesses = totalAccesses;

            DayInfo = new List<DayEntry>();
        }

        public string RoleGuid { get;set; }
        public string RoleDescription { get;set; }

        public string AuthenticatedUserName { get; }

        public int TotalAccesses { get; }

        public DateTimeOffset InsertDate { get; }
        public DateTimeOffset LastAccess { get; }

        public string IpAddress { get; }

        public string ClientName { get; }


        public Guid TenantId { get; }



        public List<DayEntry> DayInfo { get; }



        public string SourceFile { get;set; }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"User Name: {AuthenticatedUserName}");
            sb.AppendLine($"IP Address: {IpAddress}");
            if (ClientName != null)
            {
                sb.AppendLine($"Client Name: {ClientName}");
            }
            sb.AppendLine($"Insert Date: {InsertDate:yyyy-MM-dd HH:mm:ss.fffffff}");
            sb.AppendLine($"Last Access Date: {LastAccess:yyyy-MM-dd HH:mm:ss.fffffff}");
            sb.AppendLine($"Tenant Id {TenantId}");

            if (DayInfo.Any())
            {
                foreach (var dayEntry in DayInfo)
                {
                    sb.AppendLine(dayEntry.ToString());
                }
            }
            else
            {
                sb.AppendLine("No day info present");
            }


            return sb.ToString();
        }
    }

    public class DayEntry
    {
        public DayEntry(int dayNumber, DateTimeOffset date, int count)
        {
            DayNumber = dayNumber;
            Date = date;
            Count = count;
        }

        public int DayNumber { get; }
        public DateTimeOffset Date {get;}
        public int Count { get; }

        public override string ToString()
        {
            return $"Date: {Date:yyyy-MM-dd} (Day #: {DayNumber}), Count: {Count:N0}";
        }
    }

    public class VmEntry
    {
        public VmEntry(Guid biosGuid, Guid vmGuid, DateTimeOffset creationTime, DateTimeOffset lastSeenActive, string serialNumber)
        {
            BiosGuid = biosGuid;
            VmGuid = vmGuid;
            CreationTime = creationTime;
            LastSeenActive = lastSeenActive;
            SerialNumber = serialNumber;
        }

        public Guid VmGuid { get; }
        public DateTimeOffset CreationTime { get; }
        public DateTimeOffset LastSeenActive { get; }

        public Guid BiosGuid { get; }


        public string SerialNumber { get; }

        public string SourceFile { get;set; }

        public override string ToString()
        {
            return $"VM Guid: {VmGuid} Created: {CreationTime:yyyy-MM-dd HH:mm:ss.fffffff} Last Seen: {LastSeenActive:yyyy-MM-dd HH:mm:ss.fffffff} BIOS Guid: {BiosGuid}";
        }
    }



    public class RoleAccessEntry
    {
        public RoleAccessEntry(DateTimeOffset firstSeen, DateTimeOffset lastSeen, Guid roleGuid)
        {
            FirstSeen = firstSeen;
            LastSeen = lastSeen;
            RoleGuid = roleGuid;
        }

        public Guid RoleGuid { get; }

        public string RoleDescription { get;set; }

        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; }



        public string SourceFile { get;set; }



        public override string ToString()
        {
            return $"Role Guid: {RoleGuid} First Seen: {FirstSeen:yyyy-MM-dd HH:mm:ss.fffffff} Last Seen: {LastSeen:yyyy-MM-dd HH:mm:ss.fffffff}";
        }
    }

    public class DnsEntry
    {
        public DnsEntry(DateTimeOffset lastSeen, string address, string hostName)
        {
            LastSeen = lastSeen;
            Address = address;
            HostName = hostName;
        }
        public string HostName { get; }

        public string Address { get; }


        public DateTimeOffset LastSeen { get; }

        public string SourceFile { get;set; }

        public override string ToString()
        {
            return $"Host Name: {HostName} --> {Address} Last Seen: {LastSeen:yyyy-MM-dd HH:mm:ss.fffffff}";
        }
    }

    public class ChainedDbInfo
    {
        public ChainedDbInfo(int year, string fileName)
        {
            Year = year;
            FileName = fileName?.Trim('\0');
        }

        public int Year { get; internal set; }
        public string FileName { get; internal set; }

        public override string ToString()
        {
            return $"Year: {Year}, File Name: {FileName}";
        }
    }

    public class RoleInfo
    {
        public RoleInfo(Guid roleGuid, string productName, string roleName)
        {
            RoleGuid = roleGuid;
            ProductName = productName?.Trim('\0');
            RoleName = roleName?.Trim('\0');
        }

        public Guid RoleGuid { get; internal set; }
        public string RoleName { get; internal set; }

        public string ProductName { get; internal set; }


        public override string ToString()
        {
            return $"Role Guid: {RoleGuid}, Product Name: {ProductName}, Role Name: {RoleName}";
        }
    }

    public class SystemIdentInfo
    {
        public SystemIdentInfo(DateTimeOffset creationTime, int osMajor, int osMinor, int osBuild)
        {
            CreationTime = creationTime;
            OsMajor = osMajor;
            OsMinor = osMinor;
            OsBuild = osBuild;
        }

        public DateTimeOffset CreationTime { get; }

        public int OsMajor { get; internal set; }
        public int OsMinor { get; internal set; }
        public int OsBuild { get; internal set; }

        //the rest of SYSTEM_IDENTITY (after the columns above, so the CSV keeps its column order)

        public string SystemDnsHostName { get; internal set; }
        public string SystemDomainName { get; internal set; }
        public string SystemManufacturer { get; internal set; }
        public string SystemProductName { get; internal set; }
        public string SystemSerialNumber { get; internal set; }
        public string SystemSmbiosUuid { get; internal set; }
        public string OsSerialNumber { get; internal set; }

        /// <summary>
        ///     As stored (text)
        /// </summary>
        public string OsLastBootUpTime { get; internal set; }

        public string OsCountryCode { get; internal set; }

        /// <summary>
        ///     Minutes from UTC
        /// </summary>
        public short? OsCurrentTimeZone { get; internal set; }

        public bool? OsDaylightInEffect { get; internal set; }

        /// <summary>
        ///     1 workstation, 2 domain controller, 3 server
        /// </summary>
        public byte? OsProductType { get; internal set; }

        public ushort? OsSuiteMask { get; internal set; }
        public ushort? OsPlatformId { get; internal set; }
        public byte? ServicePackMajor { get; internal set; }
        public byte? ServicePackMinor { get; internal set; }
        public byte? PhysicalProcessorCount { get; internal set; }
        public byte? CoresPerPhysicalProcessor { get; internal set; }
        public byte? LogicalProcessorsPerPhysicalProcessor { get; internal set; }
        public long? MaximumMemory { get; internal set; }

        public override string ToString()
        {
            return $"{CreationTime.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fffffff}, OS Major: {OsMajor}, OS Minor: {OsMinor}, OS Build: {OsBuild}, Host: {SystemDnsHostName}, Domain: {SystemDomainName}";
        }
    }
}
