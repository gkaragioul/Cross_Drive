using MacMount.RawDiskEngine;

namespace MacMount.HfsWriteTest;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var imageFile = Path.Combine(Path.GetTempPath(), "hfsplus_test.img");
        Console.WriteLine($"HFS+ Write Test Harness");
        Console.WriteLine($"Image file: {imageFile}");
        Console.WriteLine(new string('=', 60));

        try
        {
            var allPassed = await HfsPlusWriteTests.RunAllAsync(imageFile);
            Console.WriteLine(new string('=', 60));
            Console.WriteLine(allPassed ? "ALL TESTS PASSED" : "SOME TESTS FAILED");
            return allPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: Unhandled exception: {ex}");
            return 2;
        }
        finally
        {
            // Cleanup
            if (File.Exists(imageFile))
            {
                try { File.Delete(imageFile); } catch { /* best effort */ }
            }
        }
    }
}
