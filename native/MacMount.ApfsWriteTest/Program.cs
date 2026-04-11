namespace MacMount.ApfsWriteTest;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("APFS Spaceman Write Test Harness");
        Console.WriteLine(new string('=', 60));

        try
        {
            var allPassed = await ApfsSpacemanTests.RunAllAsync();
            Console.WriteLine(new string('=', 60));
            Console.WriteLine(allPassed ? "ALL TESTS PASSED" : "SOME TESTS FAILED");
            return allPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: Unhandled exception: {ex}");
            return 2;
        }
    }
}
