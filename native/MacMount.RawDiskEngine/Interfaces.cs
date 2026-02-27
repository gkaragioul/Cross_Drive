using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MacMount.RawDiskEngine;

public interface IRawBlockDevice : IDisposable
{
    string DevicePath { get; }
    long Length { get; }
    ValueTask<int> ReadAsync(long offset, byte[] buffer, int count, CancellationToken cancellationToken = default);
}

public interface IRawBlockDeviceFactory
{
    Task<IRawBlockDevice> OpenReadOnlyAsync(string physicalDrivePath, CancellationToken cancellationToken = default);
}

public interface IFileSystemParser
{
    string Name { get; }
    Task<bool> CanHandleAsync(IRawBlockDevice device, CancellationToken cancellationToken = default);
    Task<MountPlan> BuildMountPlanAsync(IRawBlockDevice device, CancellationToken cancellationToken = default);
    IAsyncEnumerable<FileEntry> EnumerateRootAsync(IRawBlockDevice device, CancellationToken cancellationToken = default);
}

public interface IRawDiskEngine
{
    Task<MountPlan> AnalyzeAsync(MountRequest request, CancellationToken cancellationToken = default);
    Task<IRawFileSystemProvider> CreateFileSystemProviderAsync(MountPlan plan, CancellationToken cancellationToken = default);
}
