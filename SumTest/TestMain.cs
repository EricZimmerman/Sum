using System;

using NUnit.Framework;
using Serilog;
using SumData;

namespace SumTest;

[TestFixture]
public class TestMain
{

    //


    [Test]
    public void Testing2()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Information()
            .CreateLogger();

        //var r = new Srum(@"D:\OneDrive\HPSpectreSrum\Windows\System32\SRU\SRUDB.dat",@"D:\OneDrive\HPSpectreSrum\Windows\System32\config\SOFTWARE");

        //  Sum.DumpTableInfo(@"C:\Temp\tout\c\Windows\System32\LogFiles\Sum\Current.mdb");


       

        try
        {
            var r = new Sum(@"C:\Temp\DC2\c\Windows\System32\LogFiles\Sum");
                
            Console.WriteLine();

            Log.Fatal("SYSTEM IDENTITY INFO");
            foreach (var rSystemIdentityInfo in r.SystemIdentityInfos)
            {
                Log.Information("{SystemIdentityInfo}",rSystemIdentityInfo);
            }
            Console.WriteLine();

            Log.Fatal("SYSTEM ROLE INFO");
            foreach (var rRole in r.RoleInfos)
            {
                Log.Information("Role",rRole);
            }
            Console.WriteLine();

            Log.Fatal("SYSTEM CHAINED DATABASE INFO");
            foreach (var chained in r.ChainedDbs)
            {
                Log.Information("{Chained}",chained);
            }
            Console.WriteLine();

            Log.Fatal("PROCESSED DATABASE INFO");
            foreach (var processed in r.ProcessedDatabases)
            {
                Log.Information("{Processed}",processed);
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
      
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Information()
            .CreateLogger();

        //var r = new Srum(@"D:\OneDrive\HPSpectreSrum\Windows\System32\SRU\SRUDB.dat",@"D:\OneDrive\HPSpectreSrum\Windows\System32\config\SOFTWARE");

        //  Sum.DumpTableInfo(@"C:\Temp\tout\c\Windows\System32\LogFiles\Sum\Current.mdb");

        

        try
        {
            var r = new Sum(@"C:\Temp\tout\c\Windows\System32\LogFiles\Sum");
                
            Console.WriteLine();

            Log.Fatal("SYSTEM IDENTITY INFO");
            foreach (var rSystemIdentityInfo in r.SystemIdentityInfos)
            {
                Log.Information("{Thing}",rSystemIdentityInfo);
            }
            Console.WriteLine();

            Log.Fatal("SYSTEM ROLE INFO");
            foreach (var rRole in r.RoleInfos)
            {
                Log.Information("{Thing}",rRole);
            }
            Console.WriteLine();

            Log.Fatal("SYSTEM CHAINED DATABASE INFO");
            foreach (var chained in r.ChainedDbs)
            {
                Log.Information("{Thing}",chained);
            }
            Console.WriteLine();

            Log.Fatal("PROCESSED DATABASE INFO");
            foreach (var processed in r.ProcessedDatabases)
            {
                Log.Information("{Processed}",processed);
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