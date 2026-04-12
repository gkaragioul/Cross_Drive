namespace MacMount.ApfsWriteTest;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var allPassed = true;

        async Task<bool> RunSuite(string suiteName, Func<Task<bool>> runner)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {suiteName} ===");
            Console.WriteLine(new string('-', 60));
            try
            {
                return await runner();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: Unhandled exception in {suiteName}: {ex}");
                return false;
            }
        }

        try
        {
            Console.WriteLine("APFS Write Test Harness");
            Console.WriteLine(new string('=', 60));

            var spaceman  = await RunSuite("Phase 1 — Spaceman Parser",         ApfsSpacemanTests.RunAllAsync);
            var cow       = await RunSuite("Phase 2 — COW Block Writer",        ApfsCowTests.RunAllAsync);
            var fileOps   = await RunSuite("Phase 3/4 — File Operation Writes", ApfsFileOpsTests.RunAllAsync);

            allPassed = spaceman && cow && fileOps;

            Console.WriteLine();
            Console.WriteLine(new string('=', 60));
            Console.WriteLine(allPassed ? "ALL SUITES PASSED" : "SOME SUITES FAILED");
            return allPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: Unhandled exception: {ex}");
            return 2;
        }
    }
}
