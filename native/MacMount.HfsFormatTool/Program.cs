using MacMount.RawDiskEngine;

namespace MacMount.HfsFormatTool;

public static class Program
{
    // Usage: HfsFormatTool <diskNumber> <partitionOffset> <partitionSize> <volumeLabel>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: HfsFormatTool <diskNumber> <partitionOffset> <partitionSize> <volumeLabel>");
            return 2;
        }

        if (!int.TryParse(args[0], out var diskNumber) || diskNumber < 0)
        {
            Console.Error.WriteLine($"Invalid disk number: {args[0]}");
            return 2;
        }
        if (!long.TryParse(args[1], out var partitionOffset) || partitionOffset < 0)
        {
            Console.Error.WriteLine($"Invalid partition offset: {args[1]}");
            return 2;
        }
        if (!long.TryParse(args[2], out var partitionSize) || partitionSize <= 0)
        {
            Console.Error.WriteLine($"Invalid partition size: {args[2]}");
            return 2;
        }
        var volumeLabel = args[3];

        var path = $@"\\.\PhysicalDrive{diskNumber}";
        Console.WriteLine($"Opening {path} read-write...");

        using var device = WindowsRawBlockDevice.OpenReadWrite(path);
        Console.WriteLine($"Device length: {device.Length:N0} bytes");
        Console.WriteLine($"Partition: offset={partitionOffset:N0}, size={partitionSize:N0}, label='{volumeLabel}'");

        if (partitionOffset + partitionSize > device.Length)
        {
            Console.Error.WriteLine("Partition extends beyond device length.");
            return 3;
        }

        Console.WriteLine("Calling HfsPlusNativeReader.FormatAsync...");
        await HfsPlusNativeReader.FormatAsync(device, partitionOffset, partitionSize, volumeLabel);
        Console.WriteLine("FormatAsync returned successfully.");

        Console.WriteLine("Done.");
        return 0;
    }
}
