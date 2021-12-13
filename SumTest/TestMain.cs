using System;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using SumData;

namespace SumTest;

[TestFixture]
public class TestMain
{

    //


    [Test]
    public void Testing2()
    {
        var config = new LoggingConfiguration();
        var loglevel = LogLevel.Info;

        var layout = @"${level}: ${message}";

        var consoleTarget = new ColoredConsoleTarget();

        config.AddTarget("console", consoleTarget);

        consoleTarget.Layout = layout;

        var rule1 = new LoggingRule("*", loglevel, consoleTarget);
        config.LoggingRules.Add(rule1);

        LogManager.Configuration = config;

        //var r = new Srum(@"D:\OneDrive\HPSpectreSrum\Windows\System32\SRU\SRUDB.dat",@"D:\OneDrive\HPSpectreSrum\Windows\System32\config\SOFTWARE");

        //  Sum.DumpTableInfo(@"C:\Temp\tout\c\Windows\System32\LogFiles\Sum\Current.mdb");


        var l = LogManager.GetCurrentClassLogger();

        try
        {
            var r = new Sum(@"C:\Temp\DC2\c\Windows\System32\LogFiles\Sum");
                
            Console.WriteLine();

            l.Fatal("SYSTEM IDENTITY INFO");
            foreach (var rSystemIdentityInfo in r.SystemIdentityInfos)
            {
                l.Info(rSystemIdentityInfo);
            }
            Console.WriteLine();

            l.Fatal("SYSTEM ROLE INFO");
            foreach (var rRole in r.RoleInfos)
            {
                l.Info(rRole);
            }
            Console.WriteLine();

            l.Fatal("SYSTEM CHAINED DATABASE INFO");
            foreach (var chained in r.ChainedDbs)
            {
                l.Info(chained);
            }
            Console.WriteLine();

            l.Fatal("PROCESSED DATABASE INFO");
            foreach (var processed in r.ProcessedDatabases)
            {
                l.Info(processed);
            }

            Console.WriteLine("____________________________-------------------------------------____________________________");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            // throw;
        }

    }


    [Test]
    public void Testing()
    {
        var config = new LoggingConfiguration();
        var loglevel = LogLevel.Debug;

        var layout = @"${level}: ${message}";

        var consoleTarget = new ColoredConsoleTarget();

        config.AddTarget("console", consoleTarget);

        consoleTarget.Layout = layout;

        var rule1 = new LoggingRule("*", loglevel, consoleTarget);
        config.LoggingRules.Add(rule1);

        LogManager.Configuration = config;

        //var r = new Srum(@"D:\OneDrive\HPSpectreSrum\Windows\System32\SRU\SRUDB.dat",@"D:\OneDrive\HPSpectreSrum\Windows\System32\config\SOFTWARE");

        //  Sum.DumpTableInfo(@"C:\Temp\tout\c\Windows\System32\LogFiles\Sum\Current.mdb");


        var l = LogManager.GetCurrentClassLogger();

        try
        {
            var r = new Sum(@"C:\Temp\tout\c\Windows\System32\LogFiles\Sum");
                
            Console.WriteLine();

            l.Fatal("SYSTEM IDENTITY INFO");
            foreach (var rSystemIdentityInfo in r.SystemIdentityInfos)
            {
                l.Info(rSystemIdentityInfo);
            }
            Console.WriteLine();

            l.Fatal("SYSTEM ROLE INFO");
            foreach (var rRole in r.RoleInfos)
            {
                l.Info(rRole);
            }
            Console.WriteLine();

            l.Fatal("SYSTEM CHAINED DATABASE INFO");
            foreach (var chained in r.ChainedDbs)
            {
                l.Info(chained);
            }
            Console.WriteLine();

            l.Fatal("PROCESSED DATABASE INFO");
            foreach (var processed in r.ProcessedDatabases)
            {
                l.Info(processed);
            }

            Console.WriteLine("____________________________-------------------------------------____________________________");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            // throw;
        }

            

         
    }

}