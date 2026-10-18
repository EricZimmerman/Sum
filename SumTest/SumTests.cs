using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using SumData;

namespace SumTest;

[TestFixture]
[NonParallelizable]
public class SumTests
{
    private static readonly Guid FileServer = Guid.Parse("10a9226f-50ee-49d8-a393-9a501d47ce04");
    private static readonly Guid WebServer = Guid.Parse("d6256cf7-98fb-4eb4-aa18-303f1da1f770");
    private const string ChainedName = "{9D2CCF2E-F5C3-47F3-9BFA-49937CC468C0}.mdb";

    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sumtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static UalBuilder.Client Client(Guid role, string user, int year, IDictionary<int, ushort> days = null,
        uint total = 5, byte[] address = null, string clientName = null)
    {
        return new UalBuilder.Client(role, user, clientName, address ?? new byte[] {10, 0, 0, 5},
            UalBuilder.FileTime(year, 1, 2), UalBuilder.FileTime(year, 3, 4), total, days ?? new Dictionary<int, ushort>());
    }

    private void StandardFolder(bool dirtyCurrent = false)
    {
        UalBuilder.CreateSystemIdentity(P("SystemIdentity.mdb"), new[] {(FileServer, "File Server"), (WebServer, "Web Server")},
            new[] {((ushort) 2019, ChainedName)});

        UalBuilder.CreateUalDatabase(P("Current.mdb"), new[]
        {
            Client(FileServer, "CORP\\alice", 2020, new Dictionary<int, ushort> {{1, 3}, {60, 40000}, {366, 1}}),
            Client(WebServer, "CORP\\bob", 2020, address: Enumerable.Range(1, 16).Select(i => (byte) i).ToArray())
        }, new[] {("10.0.0.5", "pc1.corp.example", UalBuilder.FileTime(2020, 3, 1))}, new[] {(FileServer, UalBuilder.FileTime(2020, 1, 1), UalBuilder.FileTime(2020, 3, 4))}, dirtyCurrent);

        UalBuilder.CreateUalDatabase(P(ChainedName), new[]
        {
            Client(FileServer, "CORP\\carol", 2019, new Dictionary<int, ushort> {{32, 7}})
        });
    }

    private static Dictionary<string, string> Snapshot(string dir)
    {
        using var sha = SHA1.Create();
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(f))));
    }

    [Test]
    public void ReadsEveryDatabaseAndTable()
    {
        StandardFolder();

        var sum = new Sum(_dir);

        sum.ParseErrors.Should().BeEmpty();
        sum.FailedDatabases.Should().BeEmpty();
        sum.RoleInfos.Select(r => r.RoleName).Should().BeEquivalentTo("File Server", "Web Server");
        sum.ChainedDbs.Should().ContainSingle().Which.Year.Should().Be(2019);
        sum.ProcessedDatabases.Should().HaveCount(2);

        var si = sum.SystemIdentityInfos.Should().ContainSingle().Subject;
        si.CreationTime.Should().Be(new DateTimeOffset(2020, 9, 17, 12, 0, 0, TimeSpan.Zero));
        si.OsMajor.Should().Be(10);
        si.OsBuild.Should().Be(40000, "OSBuildNumber is unsigned 16 bits");
        si.SystemDnsHostName.Should().Be("HOST1", "the NUL padding is removed");
        si.SystemDomainName.Should().Be("corp.example");
        si.OsCurrentTimeZone.Should().Be(-420);
        si.MaximumMemory.Should().Be(2147483648L);

        var current = sum.ProcessedDatabases.Single(d => Path.GetFileName(d.FileName) == "Current.mdb");
        current.DnsInfo.Should().ContainSingle().Which.HostName.Should().Be("pc1.corp.example");
        current.RoleAccesses.Should().ContainSingle().Which.LastSeen.Should().Be(new DateTimeOffset(2020, 3, 4, 12, 0, 0, TimeSpan.Zero));

        var alice = current.Clients[FileServer].Should().ContainSingle().Subject;
        alice.AuthenticatedUserName.Should().Be("CORP\\alice");
        alice.IpAddress.Should().Be("10.0.0.5");
        alice.LastAccess.Should().Be(new DateTimeOffset(2020, 3, 4, 12, 0, 0, TimeSpan.Zero));

        current.Clients[WebServer].Single().IpAddress.Should().Be("0102:0304:0506:0708:090A:0B0C:0D0E:0F10");
    }

    [Test]
    public void CurrentDayDatesUseTheYearOfTheData()
    {
        //they were dated in the year of the machine running SumECmd
        StandardFolder();

        var sum = new Sum(_dir);

        var current = sum.ProcessedDatabases.Single(d => Path.GetFileName(d.FileName) == "Current.mdb");
        current.Year.Should().Be(2020);

        var days = current.Clients[FileServer].Single().DayInfo;
        days.Select(d => d.Date.ToString("yyyy-MM-dd")).Should().Equal("2020-01-01", "2020-02-29", "2020-12-31");
        days.Select(d => d.Count).Should().Equal(3, 40000, 1);

        var chained = sum.ProcessedDatabases.Single(d => Path.GetFileName(d.FileName) == ChainedName);
        chained.Year.Should().Be(2019);
        chained.YearSource.Should().Be("CHAINED_DATABASES");
        chained.Clients[FileServer].Single().DayInfo.Single().Date.Should().Be(new DateTimeOffset(2019, 2, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public void TheFolderIsNotChanged()
    {
        StandardFolder();
        foreach (var f in Directory.GetFiles(_dir))
        {
            File.SetAttributes(f, FileAttributes.ReadOnly);
        }

        var before = Snapshot(_dir);

        var sum = new Sum(_dir);

        sum.ProcessedDatabases.Should().HaveCount(2);
        Snapshot(_dir).Should().BeEquivalentTo(before);

        foreach (var f in Directory.GetFiles(_dir))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    [Test]
    public void ADirtyDatabaseIsBroughtUpToDateFromItsLogs()
    {
        StandardFolder(dirtyCurrent: true);
        Directory.GetFiles(_dir, "Svc*.log").Should().NotBeEmpty();

        var before = Snapshot(_dir);

        var sum = new Sum(_dir);

        sum.FailedDatabases.Should().BeEmpty();
        sum.Warnings.Should().BeEmpty("the logs were replayed: no repair");
        sum.ProcessedDatabases.Single(d => Path.GetFileName(d.FileName) == "Current.mdb").Clients.Values.SelectMany(c => c)
            .Select(c => c.AuthenticatedUserName).Should().BeEquivalentTo("CORP\\alice", "CORP\\bob");

        Snapshot(_dir).Should().BeEquivalentTo(before, "the logs are replayed into copies");
    }

    [Test]
    public void ADirtyDatabaseWithoutItsLogsIsRepairedFromACopy()
    {
        StandardFolder(dirtyCurrent: true);
        foreach (var f in Directory.GetFiles(_dir).Where(f => !f.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(f);
        }

        var sum = new Sum(_dir);

        sum.FailedDatabases.Should().BeEmpty();
        sum.Warnings.Should().ContainSingle().Which.Should().Contain("esentutl /p");
        sum.ProcessedDatabases.Should().Contain(d => Path.GetFileName(d.FileName) == "Current.mdb");
        sum.SystemIdentityInfos.Should().ContainSingle();
    }

    [Test]
    public void ADatabaseThatCantBeOpenedDoesNotLoseTheOthers()
    {
        //one bad database lost everything (the exception ended the constructor)
        StandardFolder();
        File.WriteAllBytes(P("Current.mdb"), Enumerable.Repeat((byte) 0x41, 64 * 1024).ToArray());

        var sum = new Sum(_dir);

        sum.FailedDatabases.Should().ContainSingle().Which.Should().EndWith("Current.mdb");
        sum.ParseErrors.Should().Contain(e => e.Contains("Current.mdb") && e.Contains("could not be opened"));
        sum.RoleInfos.Should().HaveCount(2);
        sum.SystemIdentityInfos.Should().ContainSingle();
        sum.ProcessedDatabases.Should().ContainSingle().Which.FileName.Should().EndWith(ChainedName);
    }

    [Test]
    public void WithoutSystemIdentityTheOtherDatabasesAreStillRead()
    {
        StandardFolder();
        File.Delete(P("SystemIdentity.mdb"));

        var sum = new Sum(_dir);

        sum.FailedDatabases.Should().ContainSingle().Which.Should().EndWith("SystemIdentity.mdb");
        sum.ProcessedDatabases.Should().HaveCount(2, "the chained database is found without CHAINED_DATABASES");
        sum.ProcessedDatabases.Single(d => d.FileName.EndsWith(ChainedName)).Year.Should().Be(2019, "from its data");
    }

    [Test]
    public void BadValuesKeepTheirRowsAndTheOtherRows()
    {
        UalBuilder.CreateSystemIdentity(P("SystemIdentity.mdb"), new[] {(FileServer, "File Server")}, Array.Empty<(ushort, string)>());
        UalBuilder.CreateUalDatabase(P("Current.mdb"), new[]
        {
            new UalBuilder.Client(FileServer, "CORP\\bad", null, new byte[] {1, 2, 3, 4}, -5, long.MaxValue, 3_000_000_000,
                new Dictionary<int, ushort>()),
            Client(FileServer, "CORP\\good", 2021)
        });

        var sum = new Sum(_dir);

        var clients = sum.ProcessedDatabases.Single().Clients[FileServer];
        clients.Select(c => c.AuthenticatedUserName).Should().Equal("CORP\\bad", "CORP\\good");

        var bad = clients[0];
        bad.InsertDate.Should().Be(DateTimeOffset.MinValue);
        bad.LastAccess.Should().Be(DateTimeOffset.MinValue);
        bad.TotalAccesses.Should().Be(int.MaxValue);
        sum.ParseErrors.Should().HaveCount(3);
        sum.ProcessedDatabases.Single().Year.Should().Be(2021);
    }

    [Test]
    public void NoUalDatabaseIsAnError()
    {
        File.WriteAllText(P("other.txt"), "x");

        var act = () => new Sum(_dir);

        act.Should().Throw<FileNotFoundException>();
        new Action(() => new Sum(P("missing"))).Should().Throw<DirectoryNotFoundException>();
    }

    /// <summary>
    ///     Writes the test folders to SUM_SYNTHETIC (clean/, dirty/), for comparing SumECmd versions
    /// </summary>
    [Test]
    [Explicit]
    public void WriteSyntheticFolders()
    {
        var target = Environment.GetEnvironmentVariable("SUM_SYNTHETIC");
        if (string.IsNullOrEmpty(target))
        {
            Assert.Ignore("SUM_SYNTHETIC not set");
        }

        foreach (var (name, dirty) in new[] {("clean", false), ("dirty", true)})
        {
            var dir = _dir;
            _dir = Path.Combine(target, name);
            Directory.CreateDirectory(_dir);
            StandardFolder(dirty);
            _dir = dir;
        }
    }

    [Test]
    public void NoTemporaryFoldersAreLeft()
    {
        StandardFolder();
        var before = Directory.GetDirectories(Path.GetTempPath(), "SumData-*").Length;

        _ = new Sum(_dir);

        Directory.GetDirectories(Path.GetTempPath(), "SumData-*").Length.Should().Be(before);
    }
}
