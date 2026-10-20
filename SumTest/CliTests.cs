using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SumTest;

/// <summary>
///     The built SumECmd.dll, run as a separate process
/// </summary>
[TestFixture]
[NonParallelizable]
public class CliTests
{
    private static readonly Guid FileServer = Guid.Parse("10a9226f-50ee-49d8-a393-9a501d47ce04");

    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sumecmd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "sum"));
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

    //the user's temp folder: an empty one, which must stay empty (the copies go to --temp or Temp next to the program)
    private string SystemTemp => Path.Combine(_dir, "systemp");

    private static string TempNextToProgram => Path.Combine(TestContext.CurrentContext.TestDirectory, "Temp");

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        Directory.CreateDirectory(SystemTemp);
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
        {
            start.Environment[name] = SystemTemp;
        }

        start.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "SumECmd.dll"));
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var p = Process.Start(start)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(120_000).Should().BeTrue();
        Directory.GetFileSystemEntries(SystemTemp).Should().BeEmpty("nothing goes to the user's temp folder");
        if (Directory.Exists(TempNextToProgram))
        {
            Directory.GetDirectories(TempNextToProgram, "SumData-*").Should().BeEmpty("the copies are deleted");
        }

        return (p.ExitCode, output + err.Result);
    }

    private void Databases()
    {
        var sum = Path.Combine(_dir, "sum");
        UalBuilder.CreateSystemIdentity(Path.Combine(sum, "SystemIdentity.mdb"), new[] {(FileServer, "File Server")}, Array.Empty<(ushort, string)>());
        UalBuilder.CreateUalDatabase(Path.Combine(sum, "Current.mdb"), new[]
        {
            new UalBuilder.Client(FileServer, "CORP\\alice", null, new byte[] {10, 0, 0, 5}, UalBuilder.FileTime(2020, 1, 2),
                UalBuilder.FileTime(2020, 3, 4), 7, new Dictionary<int, ushort> {{2, 4}})
        });
    }

    private static string[] Csv(string dir, string name)
    {
        var file = Directory.GetFiles(dir, $"*_SumECmd_{name}_Output.csv").Should().ContainSingle().Subject;
        return File.ReadAllLines(file);
    }

    [Test]
    public void WritesTheCsvsWithTheirColumnsInOrder()
    {
        Databases();
        var csv = Path.Combine(_dir, "out");

        var (code, output) = Run("-d", Path.Combine(_dir, "sum"), "--csv", csv);

        code.Should().Be(0, output);

        //the existing columns first, in their order (Timeline Explorer matches these headers); new ones at the end
        Csv(csv, "SUMMARY_SystemIdentInfo")[0].Should().StartWith("CreationTime,OsMajor,OsMinor,OsBuild,SystemDnsHostName,SystemDomainName,");
        Csv(csv, "SUMMARY_RoleInfos")[0].Should().Be("RoleGuid,RoleName,ProductName");
        Csv(csv, "SUMMARY_ChainedDbInfo")[0].Should().Be("Year,FileName");

        var clients = Csv(csv, "DETAIL_Clients");
        clients[0].Should().Be("RoleGuid,RoleDescription,AuthenticatedUserName,TotalAccesses,InsertDate,LastAccess,IpAddress,ClientName,TenantId,SourceFile");
        clients[1].Should().Be("10a9226f-50ee-49d8-a393-9a501d47ce04,File Server,CORP\\alice,7,2020-01-02 12:00:00,2020-03-04 12:00:00,10.0.0.5,,00000000-0000-0000-0000-000000000000,Current.mdb");

        var detail = Csv(csv, "DETAIL_ClientsDetailed");
        detail[0].Should().Be("Date,Count,DayNumber,RoleGuid,RoleDescription,AuthenticatedUserName,TotalAccesses,InsertDate,LastAccess,IpAddress,ClientName,TenantId,SourceFile");
        detail[1].Should().StartWith("2020-01-02,4,2,", "Current.mdb's year comes from its data");
    }

    [Test]
    public void ExitCodes()
    {
        Run().ExitCode.Should().Be(1, "-d is required");
        Run("-d", Path.Combine(_dir, "missing"), "--csv", Path.Combine(_dir, "out")).ExitCode.Should().Be(1);
        Run("-d", Path.Combine(_dir, "sum")).ExitCode.Should().Be(1, "--csv is required");
        Run("-d", Path.Combine(_dir, "sum"), "--csv", Path.Combine(_dir, "out")).ExitCode.Should().Be(1, "there are no databases");

        Databases();
        File.WriteAllBytes(Path.Combine(_dir, "sum", "Current.mdb"), new byte[8192]);
        var (code, output) = Run("-d", Path.Combine(_dir, "sum"), "--csv", Path.Combine(_dir, "out"));
        code.Should().Be(1, "Current.mdb can't be opened");
        output.Should().Contain("Current.mdb");
        Directory.GetFiles(Path.Combine(_dir, "out"), "*SystemIdentInfo*").Should().ContainSingle("what could be read is exported");
    }
    [Test]
    public void CopiesGoToTempNextToTheProgram()
    {
        Databases();
        var existed = Directory.Exists(TempNextToProgram);

        var (code, output) = Run("-d", Path.Combine(_dir, "sum"), "--csv", Path.Combine(_dir, "out"));

        code.Should().Be(0, output);
        //the copies' folder is deleted, and Temp too when this run made it and it is empty
        if (Directory.Exists(TempNextToProgram))
        {
            existed.Should().BeTrue("Temp is removed when the run created it and it is empty");
        }
    }

    [Test]
    public void CopiesGoToTheTempGiven()
    {
        Databases();
        var temp = Directory.CreateDirectory(Path.Combine(_dir, "my temp")).FullName;

        var (code, output) = Run("-d", Path.Combine(_dir, "sum"), "--csv", Path.Combine(_dir, "out"), "--temp", temp);

        code.Should().Be(0, output);
        Directory.Exists(temp).Should().BeTrue("a folder that was there is kept");
        Directory.GetFileSystemEntries(temp).Should().BeEmpty("the copies are deleted when done");

        //one that wasn't there is created, and removed again
        var newTemp = Path.Combine(_dir, "new temp");
        Run("-d", Path.Combine(_dir, "sum"), "--csv", Path.Combine(_dir, "out2"), "--temp", newTemp).ExitCode.Should().Be(0);
        Directory.Exists(newTemp).Should().BeFalse();
    }

    [Test]
    public void TempThatCantBeWrittenStops()
    {
        Databases();
        //a folder under a file can't be created
        var file = Path.Combine(_dir, "a file");
        File.WriteAllText(file, "x");

        var (code, output) = Run("-d", Path.Combine(_dir, "sum"), "--csv", Path.Combine(_dir, "out"), "--temp", Path.Combine(file, "temp"));

        code.Should().Be(1, output);
        output.Should().Contain("Give one with --temp");
        Directory.Exists(Path.Combine(_dir, "out")).Should().BeFalse("nothing was processed");
    }
}
