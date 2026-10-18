using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Isam.Esent.Interop;

namespace SumTest;

/// <summary>
///     Builds User Access Logging databases with the schema of real ones (column names and types from a Server 2012 R2
///     SystemIdentity.mdb and Current.mdb), so every reading path can be tested without collected data
/// </summary>
internal static class UalBuilder
{
    private const JET_coltyp Guid16 = (JET_coltyp) 16;
    private const JET_coltyp UInt32 = (JET_coltyp) 14;
    private const JET_coltyp UInt16 = (JET_coltyp) 17;
    private const JET_coltyp Int64 = (JET_coltyp) 15;

    public static long FileTime(int year, int month, int day, int hour = 12)
    {
        return new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
    }

    public record Client(Guid RoleGuid, string User, string ClientName, byte[] Address, long InsertDate, long LastAccess,
        uint TotalAccesses, IDictionary<int, ushort> Days);

    /// <summary>
    ///     Creates a database. <paramref name="dirty" />: ESE is ended abruptly, so the database stays in dirty shutdown
    ///     with its Svc*.log files next to it
    /// </summary>
    public static void Create(string path, Action<JET_SESID, JET_DBID> fill, bool dirty = false)
    {
        var folder = Path.GetDirectoryName(path);
        var logs = dirty ? folder : Path.Combine(Path.GetTempPath(), $"ualbuilder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logs);

        Api.JetCreateInstance(out var instance, $"ualbuilder-{Guid.NewGuid():N}");
        var p = new InstanceParameters(instance)
        {
            BaseName = "Svc",
            LogFileDirectory = logs,
            SystemDirectory = logs,
            TempDirectory = logs,
            CreatePathIfNotExist = true,
            NoInformationEvent = true,
            CircularLog = false
        };
        Api.JetInit(ref instance);

        Api.JetBeginSession(instance, out var sesid, null, null);
        Api.JetCreateDatabase(sesid, path, null, out var dbid, CreateDatabaseGrbit.OverwriteExisting);

        fill(sesid, dbid);

        if (dirty)
        {
            //committed to the logs, not detached: what a copy taken while the service runs looks like
            //JET_bitTermDirty: no flush, no clean shutdown (Abrupt alone still left it clean)
            try
            {
                Api.JetTerm2(instance, (TermGrbit) 0x8);
            }
            catch (EsentDirtyShutdownException)
            {
                //what was asked for
            }

            return;
        }

        Api.JetCloseDatabase(sesid, dbid, CloseDatabaseGrbit.None);
        Api.JetDetachDatabase(sesid, path);
        Api.JetEndSession(sesid, EndSessionGrbit.None);
        Api.JetTerm(instance);

        try
        {
            Directory.Delete(logs, true);
        }
        catch (IOException)
        {
        }
    }

    public static void CreateTable(JET_SESID s, JET_DBID db, string name, params (string Name, JET_coltyp Type, bool Tagged)[] columns)
    {
        Api.JetBeginTransaction(s);
        Api.JetCreateTable(s, db, name, 1, 100, out var table);

        foreach (var (colName, type, tagged) in columns)
        {
            var def = new JET_COLUMNDEF
            {
                coltyp = type,
                cp = type == JET_coltyp.LongText || type == JET_coltyp.Text ? JET_CP.Unicode : JET_CP.None,
                grbit = tagged ? ColumndefGrbit.ColumnTagged : ColumndefGrbit.None
            };
            Api.JetAddColumn(s, table, colName, def, null, 0, out _);
        }

        Api.JetCloseTable(s, table);
        Api.JetCommitTransaction(s, CommitTransactionGrbit.None);
    }

    public static void Insert(JET_SESID s, JET_DBID db, string tableName, Action<Table, IDictionary<string, JET_COLUMNID>> set)
    {
        using var table = new Table(s, db, tableName, OpenTableGrbit.None);
        var cols = Api.GetColumnDictionary(s, table);

        Api.JetBeginTransaction(s);
        using (var update = new Update(s, table, JET_prep.Insert))
        {
            set(table, cols);
            update.Save();
        }

        Api.JetCommitTransaction(s, CommitTransactionGrbit.None);
    }

    public static void SetString(JET_SESID s, Table t, JET_COLUMNID col, string value)
    {
        if (value != null)
        {
            Api.SetColumn(s, t, col, value, Encoding.Unicode);
        }
    }

    public static void CreateSystemIdentity(string path, IEnumerable<(Guid Guid, string Name)> roles,
        IEnumerable<(ushort Year, string FileName)> chained, string hostName = "HOST1")
    {
        Create(path, (s, db) =>
        {
            CreateTable(s, db, "SYSTEM_IDENTITY",
                ("CreationTime", JET_coltyp.DateTime, false), ("OSMajor", JET_coltyp.UnsignedByte, false),
                ("OSMinor", JET_coltyp.UnsignedByte, false), ("OSBuildNumber", UInt16, false),
                ("SystemDNSHostName", JET_coltyp.LongText, true), ("SystemDomainName", JET_coltyp.LongText, true),
                ("OSCurrentTimeZone", UInt16, false), ("MaximumMemory", Int64, false));
            CreateTable(s, db, "ROLE_IDS",
                ("RoleGuid", Guid16, false), ("ProductName", JET_coltyp.LongText, true), ("RoleName", JET_coltyp.LongText, true));
            CreateTable(s, db, "CHAINED_DATABASES",
                ("FileName", JET_coltyp.Text, false), ("Year", UInt16, false));

            Insert(s, db, "SYSTEM_IDENTITY", (t, c) =>
            {
                Api.SetColumn(s, t, c["CreationTime"], FileTime(2020, 9, 17));
                Api.SetColumn(s, t, c["OSMajor"], (byte) 10);
                Api.SetColumn(s, t, c["OSMinor"], (byte) 0);
                Api.SetColumn(s, t, c["OSBuildNumber"], (ushort) 40000);
                //NUL padded, as Windows writes them
                SetString(s, t, c["SystemDNSHostName"], hostName + "\0\0");
                SetString(s, t, c["SystemDomainName"], "corp.example\0");
                //-420 minutes, as an unsigned 16 bit column holds it
                Api.SetColumn(s, t, c["OSCurrentTimeZone"], unchecked((ushort) (short) -420));
                Api.SetColumn(s, t, c["MaximumMemory"], 2147483648L);
            });

            foreach (var (guid, name) in roles)
            {
                Insert(s, db, "ROLE_IDS", (t, c) =>
                {
                    Api.SetColumn(s, t, c["RoleGuid"], guid);
                    SetString(s, t, c["ProductName"], "0997dbd9-4db4-49aa-8ec5-8f5c6ae1c870\0");
                    SetString(s, t, c["RoleName"], name + "\0");
                });
            }

            foreach (var (year, fileName) in chained)
            {
                Insert(s, db, "CHAINED_DATABASES", (t, c) =>
                {
                    SetString(s, t, c["FileName"], fileName + "\0");
                    Api.SetColumn(s, t, c["Year"], year);
                });
            }
        });
    }

    /// <summary>
    ///     Current.mdb or a chained database
    /// </summary>
    public static void CreateUalDatabase(string path, IEnumerable<Client> clients,
        IEnumerable<(string Address, string HostName, long LastSeen)> dns = null,
        IEnumerable<(Guid Role, long FirstSeen, long LastSeen)> roleAccesses = null,
        bool dirty = false)
    {
        Create(path, (s, db) =>
        {
            var clientCols = new List<(string, JET_coltyp, bool)>
            {
                ("Address", JET_coltyp.Binary, false), ("AuthenticatedUserName", JET_coltyp.LongText, true),
                ("ClientName", JET_coltyp.LongText, true), ("InsertDate", JET_coltyp.DateTime, false),
                ("LastAccess", JET_coltyp.DateTime, false), ("RoleGuid", Guid16, false),
                ("TenantId", Guid16, false), ("TotalAccesses", UInt32, false)
            };
            for (var i = 1; i <= 366; i++)
            {
                clientCols.Add(($"Day{i}", UInt16, true));
            }

            CreateTable(s, db, "CLIENTS", clientCols.ToArray());
            CreateTable(s, db, "DNS",
                ("Address", JET_coltyp.LongText, true), ("HostName", JET_coltyp.LongText, true), ("LastSeen", JET_coltyp.DateTime, false));
            CreateTable(s, db, "ROLE_ACCESS",
                ("FirstSeen", JET_coltyp.DateTime, false), ("LastSeen", JET_coltyp.DateTime, false), ("RoleGuid", Guid16, false));
            CreateTable(s, db, "VIRTUALMACHINES",
                ("BIOSGuid", Guid16, false), ("CreationTime", JET_coltyp.DateTime, false), ("LastSeenActive", JET_coltyp.DateTime, false),
                ("SerialNumber", JET_coltyp.LongText, true), ("VmGuid", Guid16, false));

            foreach (var client in clients)
            {
                Insert(s, db, "CLIENTS", (t, c) =>
                {
                    if (client.Address != null)
                    {
                        Api.SetColumn(s, t, c["Address"], client.Address);
                    }

                    SetString(s, t, c["AuthenticatedUserName"], client.User);
                    SetString(s, t, c["ClientName"], client.ClientName);
                    Api.SetColumn(s, t, c["InsertDate"], client.InsertDate);
                    Api.SetColumn(s, t, c["LastAccess"], client.LastAccess);
                    Api.SetColumn(s, t, c["RoleGuid"], client.RoleGuid);
                    Api.SetColumn(s, t, c["TenantId"], Guid.Empty);
                    Api.SetColumn(s, t, c["TotalAccesses"], client.TotalAccesses);

                    foreach (var day in client.Days)
                    {
                        Api.SetColumn(s, t, c[$"Day{day.Key}"], day.Value);
                    }
                });
            }

            foreach (var (address, hostName, lastSeen) in dns ?? Array.Empty<(string, string, long)>())
            {
                Insert(s, db, "DNS", (t, c) =>
                {
                    SetString(s, t, c["Address"], address);
                    SetString(s, t, c["HostName"], hostName);
                    Api.SetColumn(s, t, c["LastSeen"], lastSeen);
                });
            }

            foreach (var (role, firstSeen, lastSeen) in roleAccesses ?? Array.Empty<(Guid, long, long)>())
            {
                Insert(s, db, "ROLE_ACCESS", (t, c) =>
                {
                    Api.SetColumn(s, t, c["FirstSeen"], firstSeen);
                    Api.SetColumn(s, t, c["LastSeen"], lastSeen);
                    Api.SetColumn(s, t, c["RoleGuid"], role);
                });
            }
        }, dirty);
    }
}
