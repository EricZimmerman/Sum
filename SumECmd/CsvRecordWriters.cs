using System;
using CsvHelper;
using SumData;

namespace SumECmd;

/// <summary>
///     Writes the columns of a SumData type (a library type, which has no CsvHelper attributes; its properties are
///     mostly get-only, which CsvHelper 34 record types don't write): a name and a writer per column, in order
/// </summary>
internal sealed class ColumnsCsvWriter<T> : CsvRecordWriter<T>
{
    private readonly (string Name, Action<CsvWriter, T> Write)[] _columns;

    public ColumnsCsvWriter(params (string Name, Action<CsvWriter, T> Write)[] columns)
    {
        _columns = columns;
    }

    public override int FieldCount => _columns.Length;

    public override void WriteHeader(CsvWriter writer)
    {
        foreach (var column in _columns)
        {
            WriteColumnName(writer, column.Name);
        }
    }

    public override void Write(CsvWriter writer, T record)
    {
        foreach (var column in _columns)
        {
            column.Write(writer, record);
        }
    }
}

/// <summary>
///     The columns of each CSV, as CsvHelper 33 wrote them: in the order of the properties (a derived class's first),
///     time stamps in the --dt format (current culture), numbers with the invariant culture, Guids as D, empty for null
/// </summary>
internal static class SumCsv
{
    private static void Field<TValue>(this CsvWriter writer, TValue? value) where TValue : struct, IFormattable
    {
        writer.WriteField(value.HasValue ? value.Value.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    private static void Field(this CsvWriter writer, bool? value)
    {
        if (value.HasValue)
        {
            writer.WriteField(value.Value);
        }
        else
        {
            writer.WriteField((string)null);
        }
    }

    public static CsvRecordWriter<SystemIdentInfo> SystemIdentInfo(string dt) => new ColumnsCsvWriter<SystemIdentInfo>(
        ("CreationTime", (w, r) => w.WriteField(r.CreationTime.ToString(dt))),
        ("OsMajor", (w, r) => w.WriteField(r.OsMajor)),
        ("OsMinor", (w, r) => w.WriteField(r.OsMinor)),
        ("OsBuild", (w, r) => w.WriteField(r.OsBuild)),
        ("SystemDnsHostName", (w, r) => w.WriteField(r.SystemDnsHostName)),
        ("SystemDomainName", (w, r) => w.WriteField(r.SystemDomainName)),
        ("SystemManufacturer", (w, r) => w.WriteField(r.SystemManufacturer)),
        ("SystemProductName", (w, r) => w.WriteField(r.SystemProductName)),
        ("SystemSerialNumber", (w, r) => w.WriteField(r.SystemSerialNumber)),
        ("SystemSmbiosUuid", (w, r) => w.WriteField(r.SystemSmbiosUuid)),
        ("OsSerialNumber", (w, r) => w.WriteField(r.OsSerialNumber)),
        ("OsLastBootUpTime", (w, r) => w.WriteField(r.OsLastBootUpTime)),
        ("OsCountryCode", (w, r) => w.WriteField(r.OsCountryCode)),
        ("OsCurrentTimeZone", (w, r) => w.Field(r.OsCurrentTimeZone)),
        ("OsDaylightInEffect", (w, r) => w.Field(r.OsDaylightInEffect)),
        ("OsProductType", (w, r) => w.Field(r.OsProductType)),
        ("OsSuiteMask", (w, r) => w.Field(r.OsSuiteMask)),
        ("OsPlatformId", (w, r) => w.Field(r.OsPlatformId)),
        ("ServicePackMajor", (w, r) => w.Field(r.ServicePackMajor)),
        ("ServicePackMinor", (w, r) => w.Field(r.ServicePackMinor)),
        ("PhysicalProcessorCount", (w, r) => w.Field(r.PhysicalProcessorCount)),
        ("CoresPerPhysicalProcessor", (w, r) => w.Field(r.CoresPerPhysicalProcessor)),
        ("LogicalProcessorsPerPhysicalProcessor", (w, r) => w.Field(r.LogicalProcessorsPerPhysicalProcessor)),
        ("MaximumMemory", (w, r) => w.Field(r.MaximumMemory)),
        ("SourceDirectory", (w, r) => w.WriteField(r.SourceDirectory)));

    public static CsvRecordWriter<RoleInfo> RoleInfo() => new ColumnsCsvWriter<RoleInfo>(
        ("RoleGuid", (w, r) => w.WriteField(r.RoleGuid)),
        ("RoleName", (w, r) => w.WriteField(r.RoleName)),
        ("ProductName", (w, r) => w.WriteField(r.ProductName)),
        ("SourceDirectory", (w, r) => w.WriteField(r.SourceDirectory)));

    public static CsvRecordWriter<ChainedDbInfo> ChainedDbInfo() => new ColumnsCsvWriter<ChainedDbInfo>(
        ("Year", (w, r) => w.WriteField(r.Year)),
        ("FileName", (w, r) => w.WriteField(r.FileName)),
        ("SourceDirectory", (w, r) => w.WriteField(r.SourceDirectory)));

    private static (string, Action<CsvWriter, ClientEntry>)[] ClientColumns(string dt) => new (string, Action<CsvWriter, ClientEntry>)[]
    {
        ("RoleGuid", (w, r) => w.WriteField(r.RoleGuid)),
        ("RoleDescription", (w, r) => w.WriteField(r.RoleDescription)),
        ("AuthenticatedUserName", (w, r) => w.WriteField(r.AuthenticatedUserName)),
        ("TotalAccesses", (w, r) => w.WriteField(r.TotalAccesses)),
        ("InsertDate", (w, r) => w.WriteField(r.InsertDate.ToString(dt))),
        ("LastAccess", (w, r) => w.WriteField(r.LastAccess.ToString(dt))),
        ("IpAddress", (w, r) => w.WriteField(r.IpAddress)),
        ("ClientName", (w, r) => w.WriteField(r.ClientName)),
        ("TenantId", (w, r) => w.WriteField(r.TenantId)),
        ("SourceFile", (w, r) => w.WriteField(r.SourceFile)),
        ("SourceDirectory", (w, r) => w.WriteField(r.SourceDirectory))
    };

    public static CsvRecordWriter<ClientEntry> Client(string dt) => new ColumnsCsvWriter<ClientEntry>(ClientColumns(dt));

    /// <summary>The day's columns, then the client's</summary>
    public static CsvRecordWriter<ClientEntryDayDetail> ClientDetail(string dt)
    {
        var columns = new (string, Action<CsvWriter, ClientEntryDayDetail>)[]
        {
            ("Date", (w, r) => w.WriteField($"{r.Date:yyyy-MM-dd}")),
            ("Count", (w, r) => w.WriteField(r.Count)),
            ("DayNumber", (w, r) => w.WriteField(r.DayNumber))
        };

        var client = Array.ConvertAll(ClientColumns(dt), c => (c.Item1, (Action<CsvWriter, ClientEntryDayDetail>)((w, r) => c.Item2(w, r))));

        var all = new (string, Action<CsvWriter, ClientEntryDayDetail>)[columns.Length + client.Length];
        columns.CopyTo(all, 0);
        client.CopyTo(all, columns.Length);

        return new ColumnsCsvWriter<ClientEntryDayDetail>(all);
    }

    public static CsvRecordWriter<DnsEntry> Dns(string dt) => new ColumnsCsvWriter<DnsEntry>(
        ("HostName", (w, r) => w.WriteField(r.HostName)),
        ("Address", (w, r) => w.WriteField(r.Address)),
        ("LastSeen", (w, r) => w.WriteField(r.LastSeen.ToString(dt))),
        ("SourceFile", (w, r) => w.WriteField(r.SourceFile)),
        ("SourceDirectory", (w, r) => w.WriteField(r.SourceDirectory)));

    public static CsvRecordWriter<RoleAccessEntry> RoleAccess(string dt) => new ColumnsCsvWriter<RoleAccessEntry>(
        ("RoleGuid", (w, r) => w.WriteField(r.RoleGuid)),
        ("RoleDescription", (w, r) => w.WriteField(r.RoleDescription)),
        ("FirstSeen", (w, r) => w.WriteField(r.FirstSeen.ToString(dt))),
        ("LastSeen", (w, r) => w.WriteField(r.LastSeen.ToString(dt))),
        ("SourceFile", (w, r) => w.WriteField(r.SourceFile)),
        ("SourceDirectory", (w, r) => w.WriteField(r.SourceDirectory)));

    public static CsvRecordWriter<VmEntry> Vm(string dt) => new ColumnsCsvWriter<VmEntry>(
        ("VmGuid", (w, r) => w.WriteField(r.VmGuid)),
        ("CreationTime", (w, r) => w.WriteField(r.CreationTime.ToString(dt))),
        ("LastSeenActive", (w, r) => w.WriteField(r.LastSeenActive.ToString(dt))),
        ("BiosGuid", (w, r) => w.WriteField(r.BiosGuid)),
        ("SerialNumber", (w, r) => w.WriteField(r.SerialNumber)),
        ("SourceFile", (w, r) => w.WriteField(r.SourceFile)),
        ("SourceDirectory", (w, r) => w.WriteField(r.SourceDirectory)));
}
