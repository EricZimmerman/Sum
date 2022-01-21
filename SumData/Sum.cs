using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Isam.Esent.Interop;
using Serilog;


namespace SumData
{
    public class Sum
    {
        public Sum(string directory)
        {
            //https://docs.microsoft.com/en-us/previous-versions/windows/desktop/ual/msftual-hyperv
            //find systemidentity.mdb
            //get tables
            //Role_IDs for guid discovery
            //system_identity for os info
            //chained_databases for other things to look for

            //current.mdb is...current
            //for each file in chained, process, if it exists

            //for each table in systemident, dump

            SystemIdentityInfos = new List<SystemIdentInfo>();
            RoleInfos = new List<RoleInfo>();
            ChainedDbs = new List<ChainedDbInfo>();
            ProcessedDatabases = new List<ChainedDatabase>();

            if (Directory.Exists(directory) == false)
            {
                throw new DirectoryNotFoundException($"Directory '{directory}' not found!");
            }

            var mdbFiles = Directory.GetFiles(directory, "*.mdb");

            var sysIdent = Path.Combine(directory, "SystemIdentity.mdb");

            if (File.Exists(sysIdent) == false)
            {
                throw new FileNotFoundException($"File '{sysIdent}' not found!");
            }

            Log.Information("Found '{SysIdent}'. Processing...",sysIdent);

            ProcessSystemIdentityDatabase(sysIdent);

            var currentDb = Path.Combine(directory, "Current.mdb");

            if (File.Exists(currentDb) == false)
            {
                throw new FileNotFoundException($"File '{currentDb}' not found!");
            }
          
            Log.Information("Found '{CurrentDb}'. Processing...",currentDb);
            ProcessDatabase(currentDb,DateTime.Now.Year);

            foreach (var chainedDbInfo in ChainedDbs)
            {
                var chainFile = Path.Combine(directory, chainedDbInfo.FileName);

                if (File.Exists(chainFile) == false)
                {
                    Log.Information("Chained database '{ChainFile}' for year {Year} does not exist! Skipping...",chainFile,chainedDbInfo.Year);
                    continue;
                }
                
                Log.Information("Found '{ChainFile}' for year {Year}. Processing...",chainFile,chainedDbInfo.Year);
                ProcessDatabase(chainFile,chainedDbInfo.Year);
            }
        }

        public string GetRoleDescFromGuid(Guid roleGuid)
        {
            var r = RoleInfos.SingleOrDefault(t => t.RoleGuid == roleGuid);

            return r?.RoleName;
        }

        public List<SystemIdentInfo> SystemIdentityInfos { get; }
        public List<RoleInfo> RoleInfos { get; }
        public List<ChainedDbInfo> ChainedDbs { get; }

        public List<ChainedDatabase> ProcessedDatabases { get; }

        
        //esentutl.exe /r svc /a  # ?? need toi test this vs /i for current
        //esentutl.exe /p .\Current.mdb

        private void ProcessDatabase(string dbFile,int year)
        {
            using var instance = new Instance("dbFiile");
            instance.Parameters.LogFileDirectory = Path.GetDirectoryName(dbFile);
            instance.Parameters.SystemDirectory = Path.GetDirectoryName(dbFile);
            //instance.Parameters.BaseName = "SRU";
            instance.Parameters.TempDirectory = Path.GetDirectoryName(dbFile);

            //instance.Parameters.Recovery = true;

            instance.Init();

            var chainedDb = new ChainedDatabase(dbFile);

            Log.Debug("Setting up dbFile session for '{DbFile}'",dbFile);
            using var session = new Session(instance);
            Api.JetAttachDatabase(session, dbFile, AttachDatabaseGrbit.ReadOnly);
            Api.JetOpenDatabase(session, dbFile, null, out var dbid, OpenDatabaseGrbit.ReadOnly);
            
            Log.Debug("Getting Clients info");
            try
            {
                using var rolesTable = new Table(session, dbid, "CLIENTS", OpenTableGrbit.ReadOnly);

                Api.JetSetTableSequential(session, rolesTable, SetTableSequentialGrbit.None);

                Api.MoveBeforeFirst(session, rolesTable);

                while (Api.TryMoveNext(session, rolesTable))
                {
                    var authUserName = Api.RetrieveColumnAsString(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "AuthenticatedUserName")).Trim('\0');
                    var clientName = Api.RetrieveColumnAsString(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "ClientName"))?.Trim('\0');

                    var idRaw = Api.RetrieveColumnAsInt64(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "InsertDate"));
                    var insertDate = DateTimeOffset.FromFileTime(idRaw.Value).ToUniversalTime();

                    var laRaw = Api.RetrieveColumnAsInt64(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "LastAccess"));
                    var lastAccess = DateTimeOffset.FromFileTime(laRaw.Value).ToUniversalTime();
                 
                    var roleGuid = Api.RetrieveColumnAsGuid(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "RoleGuid"));
                    var tenantId = Api.RetrieveColumnAsGuid(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "TenantId"));
                    var address = Api.RetrieveColumn(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "Address"));
                    var totalAccess = Api.RetrieveColumnAsInt32(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "TotalAccesses"));

                    if (chainedDb.Clients.ContainsKey(roleGuid.Value) == false)
                    {
                        chainedDb.Clients.Add(roleGuid.Value,new List<ClientEntry>());
                    }

                    var ce = new ClientEntry(ConvertBytesToIpAddress(address), authUserName, clientName, insertDate, lastAccess, tenantId.Value, totalAccess.Value);

                    for (var i = 1; i <= 366; i++)
                    {
                       var dayVal = Api.RetrieveColumnAsInt16(session, rolesTable, Api.GetTableColumnid(session, rolesTable, $"Day{i}"));
                        
                        if (dayVal.HasValue)
                        {
                            var dt = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i - 1);

                            var de = new DayEntry(i, dt, dayVal.Value);
                         
                            ce.DayInfo.Add(de);
                        }
                    }

                    chainedDb.Clients[roleGuid.Value].Add(ce);

                     Log.Verbose("Added client info: {Ce}",ce);
                }

                Api.JetResetTableSequential(session, rolesTable, ResetTableSequentialGrbit.None);
            }
            catch (Exception e)
            {
                Log.Error(e,"Error processing Clients info: {Message}",e.Message);
            }

            Log.Debug("Getting DNS info");
            try
            {
                using var dnsTable = new Table(session, dbid, "DNS", OpenTableGrbit.ReadOnly);

                Api.JetSetTableSequential(session, dnsTable, SetTableSequentialGrbit.None);

                Api.MoveBeforeFirst(session, dnsTable);

                while (Api.TryMoveNext(session, dnsTable))
                {
                    var address = Api.RetrieveColumnAsString(session, dnsTable, Api.GetTableColumnid(session, dnsTable, "Address")).Trim('\0');
                    var hostName = Api.RetrieveColumnAsString(session, dnsTable, Api.GetTableColumnid(session, dnsTable, "HostName"))?.Trim('\0');

                    var idRaw = Api.RetrieveColumnAsInt64(session, dnsTable, Api.GetTableColumnid(session, dnsTable, "LastSeen"));
                    var lastSeen = DateTimeOffset.FromFileTime(idRaw.Value).ToUniversalTime();

                  var dns = new DnsEntry(lastSeen,address,hostName);

                    chainedDb.DnsInfo.Add(dns);
                    Log.Verbose("Added DNS info: {Dns}",dns);
                }

                Api.JetResetTableSequential(session, dnsTable, ResetTableSequentialGrbit.None);

            }
            catch (Exception e)
            {
                Log.Error(e,"Error processing DNS info: {Message}",e.Message);
            }


            Log.Debug("Getting Role Access info");
            try
            {
                using var rolesTable = new Table(session, dbid, "ROLE_ACCESS", OpenTableGrbit.ReadOnly);

                Api.JetSetTableSequential(session, rolesTable, SetTableSequentialGrbit.None);

                Api.MoveBeforeFirst(session, rolesTable);

                while (Api.TryMoveNext(session, rolesTable))
                {
                    var idRaw = Api.RetrieveColumnAsInt64(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "FirstSeen"));
                    var firstSeen = DateTimeOffset.FromFileTime(idRaw.Value).ToUniversalTime();

                    var laRaw = Api.RetrieveColumnAsInt64(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "LastSeen"));
                    var lastSeen = DateTimeOffset.FromFileTime(laRaw.Value).ToUniversalTime();
                 
                    var roleGuid = Api.RetrieveColumnAsGuid(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "RoleGuid"));

                    var re = new RoleAccessEntry(firstSeen, lastSeen, roleGuid.Value);

                    chainedDb.RoleAccesses.Add(re);
                    Log.Verbose("Added role access info: {Re}",re);
                }

                Api.JetResetTableSequential(session, rolesTable, ResetTableSequentialGrbit.None);

            }
            catch (Exception e)
            {
                Log.Error(e,"Error processing Role Access info: {Message}",e.Message);
            }

            Log.Debug("Getting Virtual Machines info");
            try
            {
                using var vmTable = new Table(session, dbid, "VIRTUALMACHINES", OpenTableGrbit.ReadOnly);

                Api.JetSetTableSequential(session, vmTable, SetTableSequentialGrbit.None);

                Api.MoveBeforeFirst(session, vmTable);

                while (Api.TryMoveNext(session, vmTable))
                {
                    var serialNumber = Api.RetrieveColumnAsString(session, vmTable, Api.GetTableColumnid(session, vmTable, "SerialNumber")).Trim('\0');

                    var idRaw = Api.RetrieveColumnAsInt64(session, vmTable, Api.GetTableColumnid(session, vmTable, "CreationTime"));
                    var creationTime = DateTimeOffset.FromFileTime(idRaw.Value).ToUniversalTime();

                    var laRaw = Api.RetrieveColumnAsInt64(session, vmTable, Api.GetTableColumnid(session, vmTable, "LastSeenActive"));
                    var lastSeenActive = DateTimeOffset.FromFileTime(laRaw.Value).ToUniversalTime();
                 
                    var biosGuid = Api.RetrieveColumnAsGuid(session, vmTable, Api.GetTableColumnid(session, vmTable, "BIOSGuid"));
                    var vmGuid = Api.RetrieveColumnAsGuid(session, vmTable, Api.GetTableColumnid(session, vmTable, "VmGuid"));

                    var vm = new VmEntry(biosGuid.Value, vmGuid.Value, creationTime, lastSeenActive, serialNumber);

                    chainedDb.VmInfo.Add(vm);

                    Log.Verbose("Added VM info: {Vm}",vm);
                }

                Api.JetResetTableSequential(session, vmTable, ResetTableSequentialGrbit.None);


            }
            catch (Exception e)
            {
                Log.Error(e,"Error processing Virtual Machine info: {Message}",e.Message);
            }
     

            //all done, so add it to list
            ProcessedDatabases.Add(chainedDb);

        }

        private string ConvertBytesToIpAddress(byte[] rawBytes)
        {
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

        private void ProcessSystemIdentityDatabase(string sysIdent)
        {
            using var instance = new Instance("sysIdent");
            instance.Parameters.LogFileDirectory = Path.GetDirectoryName(sysIdent);
            instance.Parameters.SystemDirectory = Path.GetDirectoryName(sysIdent);
            //instance.Parameters.BaseName = "SRU";
            instance.Parameters.TempDirectory = Path.GetDirectoryName(sysIdent);

            //instance.Parameters.Recovery = false;            

            instance.Init();

            Log.Debug("Setting up session for SystemIdentity");
            using var session = new Session(instance);
            Api.JetAttachDatabase(session, sysIdent, AttachDatabaseGrbit.ReadOnly);
            Api.JetOpenDatabase(session, sysIdent, null, out var dbid, OpenDatabaseGrbit.ReadOnly);

            Log.Debug("Getting System identity info");
            try
            {
                GetSystemIdentityInfo(session, dbid);
            }
            catch (Exception e)
            {
                Log.Error(e,"Error processing System identity info: {Message}",e.Message);
            }


            Log.Debug("Getting Role Id info");
            try
            {
                GetRoleIdInfo(session, dbid);
            }
            catch (Exception e)
            {
                Log.Error(e,"Error Role IDs info: {Message}",e.Message);
            }

            Log.Debug("Getting Chained database info");
            try
            {
                GetChainedDatabaseInfo(session, dbid);
            }
            catch (Exception e)
            {
                Log.Error(e,"Error processing Chained Database info: {Message}",e.Message);
            }
        }

        private void GetChainedDatabaseInfo(Session session, JET_DBID dbid)
        {
            using var rolesTable = new Table(session, dbid, "CHAINED_DATABASES", OpenTableGrbit.ReadOnly);

            Api.JetSetTableSequential(session, rolesTable, SetTableSequentialGrbit.None);

            Api.MoveBeforeFirst(session, rolesTable);

            while (Api.TryMoveNext(session, rolesTable))
            {
                var year = Api.RetrieveColumnAsInt16(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "Year"));
                var fileName = Api.RetrieveColumnAsString(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "FileName")).Trim('\0');

                var cd = new ChainedDbInfo(year.Value, fileName);

                ChainedDbs.Add(cd);

                Log.Verbose("Added chained db info: {Cd}",cd);
            }

            Api.JetResetTableSequential(session, rolesTable, ResetTableSequentialGrbit.None);
        }

        private void GetRoleIdInfo(Session session, JET_DBID dbid)
        {
            using var rolesTable = new Table(session, dbid, "ROLE_IDS", OpenTableGrbit.ReadOnly);

            Api.JetSetTableSequential(session, rolesTable, SetTableSequentialGrbit.None);

            Api.MoveBeforeFirst(session, rolesTable);

            while (Api.TryMoveNext(session, rolesTable))
            {
                var roleGuid = Api.RetrieveColumnAsGuid(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "RoleGuid"));
                var prodName = Api.RetrieveColumnAsString(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "ProductName")).Trim('\0');
                var roleName = Api.RetrieveColumnAsString(session, rolesTable, Api.GetTableColumnid(session, rolesTable, "RoleName")).Trim('\0');

                var ri = new RoleInfo(roleGuid.Value, prodName, roleName);

                RoleInfos.Add(ri);

                Log.Verbose("Added role info: {Ri}",ri);
            }

            Api.JetResetTableSequential(session, rolesTable, ResetTableSequentialGrbit.None);
        }

        private void GetSystemIdentityInfo(Session session, JET_DBID dbid)
        {
            using var systemIdent = new Table(session, dbid, "SYSTEM_IDENTITY", OpenTableGrbit.ReadOnly);

            Api.JetSetTableSequential(session, systemIdent, SetTableSequentialGrbit.None);

            Api.MoveBeforeFirst(session, systemIdent);

            while (Api.TryMoveNext(session, systemIdent))
            {
                var dtRaw = Api.RetrieveColumnAsInt64(session, systemIdent, Api.GetTableColumnid(session, systemIdent, "CreationTime"));

                var creationTime = DateTimeOffset.FromFileTime(dtRaw.Value);

                var osMajor = Api.RetrieveColumnAsByte(session, systemIdent, Api.GetTableColumnid(session, systemIdent, "OSMajor"));
                var osMinor = Api.RetrieveColumnAsByte(session, systemIdent, Api.GetTableColumnid(session, systemIdent, "OSMinor"));
                var osBuildNum = Api.RetrieveColumnAsInt16(session, systemIdent, Api.GetTableColumnid(session, systemIdent, "OSBuildNumber"));

                var si = new SystemIdentInfo(creationTime.ToUniversalTime(), osMajor.Value, osMinor.Value, osBuildNum.Value);

                SystemIdentityInfos.Add(si);

                Log.Verbose("Added system identity info: {Si}",si);
            }

            Api.JetResetTableSequential(session, systemIdent, ResetTableSequentialGrbit.None);
        }

        public static void DumpTableInfo(string fileName)
        {
            if (File.Exists(fileName) == false)
            {
                throw new FileNotFoundException($"'{fileName}' does not exist!");
            }

            using var instance = new Instance("pulldata");
            instance.Parameters.Recovery = false;

            instance.Init();

            using var session = new Session(instance);
            Api.JetAttachDatabase(session, fileName, AttachDatabaseGrbit.ReadOnly);
            Api.JetOpenDatabase(session, fileName, null, out var dbid, OpenDatabaseGrbit.ReadOnly);

            foreach (var table in Api.GetTableNames(session, dbid))
            {
                Console.WriteLine($"TABLE: {table}");

                foreach (var column in Api.GetTableColumns(session, dbid, table))
                {
                    Console.WriteLine("\t{0}: {1}", column.Name, column.Coltyp);
                }

                Console.WriteLine("------------------------------------");
            }
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
            FileName = fileName.Trim('\0');
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
            ProductName = productName.Trim('\0');
            RoleName = roleName.Trim('\0');
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

        public override string ToString()
        {
            return $"{CreationTime.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fffffff}, OS Major: {OsMajor}, OS Minor: {OsMinor}, OS Build: {OsBuild}";
        }
    }
}