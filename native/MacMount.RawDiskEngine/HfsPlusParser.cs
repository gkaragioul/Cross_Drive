using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

public sealed class HfsPlusParser : IFileSystemParser
{
    public string Name => "HFS+";

    public async Task<bool> CanHandleAsync(IRawBlockDevice device, CancellationToken cancellationToken = default)
    {
        // HFS+/HFSX volume header signature at byte offset 1024: 'H+' or 'HX'.
        // Read aligned 4KB block and inspect bytes 1024..1025.
        var block = new byte[4096];
        var read = await RawReadUtil.ReadExactlyAtAsync(device, 0, block, block.Length, cancellationToken).ConfigureAwait(false);
        if (read < 1026) return false;

        return (block[1024] == (byte)'H' && block[1025] == (byte)'+')
            || (block[1024] == (byte)'H' && block[1025] == (byte)'X');
    }

    public Task<MountPlan> BuildMountPlanAsync(IRawBlockDevice device, CancellationToken cancellationToken = default)
    {
        var plan = new MountPlan(
            device.DevicePath,
            "HFS+",
            device.Length,
            Writable: false,
            Notes: "HFS+/HFSX signature detected. Metadata parser not implemented yet."
        );
        return Task.FromResult(plan);
    }

    public async IAsyncEnumerable<FileEntry> EnumerateRootAsync(IRawBlockDevice device, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
