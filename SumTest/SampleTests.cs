using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using SumData;

namespace SumTest;

/// <summary>
///     The DFIR Artifact Museum's Windows/SUM/Win2012R2/StolenSzechuan (MIT): SUM_SAMPLES, or Corpus/SUM in a parent
///     folder of the tests. Expected values were read with libesedb. Skipped without the samples (they are not in the
///     repository)
/// </summary>
[TestFixture]
[NonParallelizable]
public class SampleTests
{
    private static string SampleDir()
    {
        var root = Environment.GetEnvironmentVariable("SUM_SAMPLES");

        if (string.IsNullOrEmpty(root))
        {
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "Corpus", "SUM");
                if (Directory.Exists(candidate))
                {
                    root = candidate;
                    break;
                }
            }
        }

        var dir = root == null ? null : Path.Combine(root, "Win2012R2", "StolenSzechuan");
        if (dir == null || !File.Exists(Path.Combine(dir, "Current.mdb")))
        {
            Assert.Ignore("SUM samples not found (SUM_SAMPLES or Corpus/SUM)");
        }

        return dir;
    }

    [Test]
    public void StolenSzechuan()
    {
        var dir = SampleDir();

        var sum = new Sum(dir);

        sum.FailedDatabases.Should().BeEmpty();
        sum.ParseErrors.Should().BeEmpty();

        //Current.mdb is in dirty shutdown and its logs were not collected: a copy is repaired
        sum.Warnings.Should().ContainSingle().Which.Should().Contain("esentutl /p");

        sum.SystemIdentityInfos.Select(s => s.CreationTime.UtcDateTime.ToFileTimeUtc())
            .Should().Equal(132448316944240000, 132448391954480000, 132449418114420000);
        sum.SystemIdentityInfos.Should().OnlyContain(s => s.OsMajor == 6 && s.OsMinor == 3 && s.OsBuild == 9600);
        sum.SystemIdentityInfos.Last().SystemDnsHostName.Should().Be("CITADEL-DC01");
        sum.SystemIdentityInfos.Last().SystemDomainName.Should().Be("C137.local");
        sum.SystemIdentityInfos.Last().SystemManufacturer.Should().Be("VMware, Inc.");

        sum.RoleInfos.Should().HaveCount(13);
        sum.GetRoleDescFromGuid(Guid.Parse("10a9226f-50ee-49d8-a393-9a501d47ce04")).Should().Be("File Server");
        sum.GetRoleDescFromGuid(Guid.Parse("d6256cf7-98fb-4eb4-aa18-303f1da1f770")).Should().Be("Web Server");

        var chained = sum.ChainedDbs.Should().ContainSingle().Subject;
        chained.Year.Should().Be(2020);
        chained.FileName.Should().Be("{9D2CCF2E-F5C3-47F3-9BFA-49937CC468C0}.mdb");

        var current = sum.ProcessedDatabases.Should().ContainSingle().Subject;
        current.Clients.Should().BeEmpty();
        current.DnsInfo.Should().BeEmpty();
        current.RoleAccesses.Should().BeEmpty();
        current.VmInfo.Should().BeEmpty();
    }
}
