using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MacMount.RawDiskEngine;

/// <summary>
/// High-performance caching layer for raw filesystem providers.
/// Implements read-ahead caching, directory entry caching, and block-level caching
/// for NVMe-like performance on raw disk access.
/// </summary>
public sealed class CachedRawFileSystemProvider : IRawFileSystemProvider
{
    private readonly IRawFileSystemProvider _inner;
    private readonly CacheOptions _options;
    
    // Directory entry cache: path -> cached directory listing
    private readonly ConcurrentDictionary<string, CachedDirectory> _dirCache;
    
    // File entry cache: path -> cached entry info
    private readonly ConcurrentDictionary<string, CachedEntry> _entryCache;
    
    // Block cache for file reads: (path, blockIndex) -> cached block
    private readonly ConcurrentDictionary<(string Path, long BlockIndex), CachedBlock> _blockCache;
    
    // Read-ahead tracking: path -> last accessed block index
    private readonly ConcurrentDictionary<string, long> _readAheadCursor;
    
    // Async read-ahead queue
    private readonly Channel<ReadAheadRequest> _readAheadChannel;
    private readonly CancellationTokenSource _readAheadCts;
    private readonly Task _readAheadTask;
    
    private long _cacheHits;
    private long _cacheMisses;
    private long _bytesReadFromCache;
    private long _bytesReadFromDisk;

    public CachedRawFileSystemProvider(IRawFileSystemProvider inner, CacheOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? CacheOptions.Default;
        
        _dirCache = new ConcurrentDictionary<string, CachedDirectory>(StringComparer.OrdinalIgnoreCase);
        _entryCache = new ConcurrentDictionary<string, CachedEntry>(StringComparer.OrdinalIgnoreCase);
        _blockCache = new ConcurrentDictionary<(string, long), CachedBlock>();
        _readAheadCursor = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        
        _readAheadChannel = Channel.CreateUnbounded<ReadAheadRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        
        _readAheadCts = new CancellationTokenSource();
        _readAheadTask = RunReadAheadWorkerAsync(_readAheadCts.Token);
    }

    public string FileSystemType => _inner.FileSystemType;
    public long TotalBytes => _inner.TotalBytes;
    public long FreeBytes => _inner.FreeBytes;

    public RawFsEntry? GetEntry(string path)
    {
        var normalized = NormalizePath(path);
        
        // Check entry cache first
        if (_entryCache.TryGetValue(normalized, out var cachedEntry))
        {
            if (!cachedEntry.IsExpired(_options.EntryCacheTtl))
            {
                Interlocked.Increment(ref _cacheHits);
                return cachedEntry.Entry;
            }
            _entryCache.TryRemove(normalized, out _);
        }
        
        // Cache miss - get from inner provider
        var entry = _inner.GetEntry(normalized);
        
        if (entry != null)
        {
            _entryCache[normalized] = new CachedEntry(entry, DateTimeOffset.UtcNow);
        }
        
        Interlocked.Increment(ref _cacheMisses);
        return entry;
    }

    public IReadOnlyList<RawFsEntry> ListDirectory(string path)
    {
        var normalized = NormalizePath(path);
        
        // Check directory cache
        if (_dirCache.TryGetValue(normalized, out var cachedDir))
        {
            if (!cachedDir.IsExpired(_options.DirectoryCacheTtl))
            {
                Interlocked.Increment(ref _cacheHits);
                // Update entry cache for all items
                foreach (var entry in cachedDir.Entries)
                {
                    _entryCache[entry.Path] = new CachedEntry(entry, DateTimeOffset.UtcNow);
                }
                return cachedDir.Entries;
            }
            _dirCache.TryRemove(normalized, out _);
        }
        
        // Cache miss - list from inner provider
        var entries = _inner.ListDirectory(normalized);
        
        if (entries.Count > 0)
        {
            _dirCache[normalized] = new CachedDirectory(entries.ToArray(), DateTimeOffset.UtcNow);
            
            // Prime entry cache
            foreach (var entry in entries)
            {
                _entryCache[entry.Path] = new CachedEntry(entry, DateTimeOffset.UtcNow);
            }
            
            // Trigger async read-ahead for files in this directory
            if (_options.EnableReadAhead)
            {
                foreach (var entry in entries.Where(e => !e.IsDirectory && e.Size > 0 && e.Size <= _options.MaxReadAheadFileSize))
                {
                    _ = Task.Run(() => PrimeReadAheadAsync(entry.Path, entry.Size));
                }
            }
        }
        
        Interlocked.Increment(ref _cacheMisses);
        return entries;
    }

    public int ReadFile(string path, long offset, Span<byte> destination)
    {
        var normalized = NormalizePath(path);
        var blockSize = _options.BlockSize;
        
        // Check if we should use block caching
        if (_options.EnableBlockCache && destination.Length <= blockSize * 4)
        {
            return ReadWithBlockCache(normalized, offset, destination);
        }
        
        // Large read - bypass cache and read directly
        var read = _inner.ReadFile(normalized, offset, destination);
        if (read > 0)
        {
            Interlocked.Add(ref _bytesReadFromDisk, read);
        }
        return read;
    }

    private int ReadWithBlockCache(string path, long offset, Span<byte> destination)
    {
        var blockSize = _options.BlockSize;
        var startBlock = offset / blockSize;
        var endBlock = (offset + destination.Length - 1) / blockSize;
        var totalRead = 0;
        var destOffset = 0;
        
        for (var blockIdx = startBlock; blockIdx <= endBlock; blockIdx++)
        {
            var blockOffset = blockIdx * blockSize;
            var offsetInBlock = offset - blockOffset;
            if (offsetInBlock < 0) offsetInBlock = 0;
            
            var bytesToRead = (int)Math.Min(
                blockSize - offsetInBlock,
                destination.Length - destOffset
            );
            
            if (bytesToRead <= 0) break;
            
            // Try to get from block cache
            var cacheKey = (path, blockIdx);
            if (_blockCache.TryGetValue(cacheKey, out var cachedBlock))
            {
                if (!cachedBlock.IsExpired(_options.BlockCacheTtl))
                {
                    // Copy from cached block
                    var sourceSpan = cachedBlock.Data.AsSpan(
                        (int)offsetInBlock,
                        bytesToRead
                    );
                    sourceSpan.CopyTo(destination.Slice(destOffset, bytesToRead));
                    
                    Interlocked.Increment(ref _cacheHits);
                    Interlocked.Add(ref _bytesReadFromCache, bytesToRead);
                    
                    totalRead += bytesToRead;
                    destOffset += bytesToRead;
                    offset = blockOffset + blockSize;
                    
                    // Trigger read-ahead for next block
                    if (_options.EnableReadAhead && blockIdx == startBlock)
                    {
                        QueueReadAhead(path, blockIdx + 1);
                    }
                    continue;
                }
                _blockCache.TryRemove(cacheKey, out _);
            }
            
            // Cache miss - read block from disk
            var blockBuffer = ArrayPool<byte>.Shared.Rent(blockSize);
            try
            {
                var blockSpan = blockBuffer.AsSpan(0, blockSize);
                var read = _inner.ReadFile(path, blockOffset, blockSpan);
                
                if (read > 0)
                {
                    Interlocked.Increment(ref _cacheMisses);
                    Interlocked.Add(ref _bytesReadFromDisk, read);
                    
                    // Copy requested portion to destination
                    var copyLength = Math.Min(bytesToRead, read - (int)offsetInBlock);
                    if (copyLength > 0)
                    {
                        blockSpan.Slice((int)offsetInBlock, copyLength).CopyTo(
                            destination.Slice(destOffset, copyLength)
                        );
                        totalRead += copyLength;
                        destOffset += copyLength;
                    }
                    
                    // Cache the full block if it was a complete read
                    if (read == blockSize && _blockCache.Count < _options.MaxCachedBlocks)
                    {
                        var cacheData = new byte[read];
                        blockSpan.Slice(0, read).CopyTo(cacheData);
                        _blockCache[cacheKey] = new CachedBlock(cacheData, DateTimeOffset.UtcNow);
                    }
                    
                    offset = blockOffset + blockSize;
                }
                else
                {
                    break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(blockBuffer);
            }
        }
        
        return totalRead;
    }

    private void PrimeReadAheadAsync(string path, long fileSize)
    {
        try
        {
            var blockSize = _options.BlockSize;
            var blocksToCache = Math.Min(
                (int)Math.Min(fileSize / blockSize, _options.ReadAheadBlocks),
                16 // Max 16 blocks per file
            );
            
            for (var i = 0; i < blocksToCache; i++)
            {
                var blockIdx = i;
                var cacheKey = (path, blockIdx);
                
                if (_blockCache.ContainsKey(cacheKey)) continue;
                
                var blockBuffer = ArrayPool<byte>.Shared.Rent(blockSize);
                try
                {
                    var blockSpan = blockBuffer.AsSpan(0, blockSize);
                    var read = _inner.ReadFile(path, blockIdx * blockSize, blockSpan);
                    
                    if (read == blockSize && _blockCache.Count < _options.MaxCachedBlocks)
                    {
                        var cacheData = new byte[read];
                        blockSpan.CopyTo(cacheData);
                        _blockCache[cacheKey] = new CachedBlock(cacheData, DateTimeOffset.UtcNow);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(blockBuffer);
                }
                

            }
        }
        catch
        {
            // Best effort only
        }
    }

    private void QueueReadAhead(string path, long nextBlockIndex)
    {
        if (_readAheadChannel.Writer.TryWrite(new ReadAheadRequest(path, nextBlockIndex)))
        {
            _readAheadCursor[path] = nextBlockIndex;
        }
    }

    private async Task RunReadAheadWorkerAsync(CancellationToken ct)
    {
        await foreach (var request in _readAheadChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                var cacheKey = (request.Path, request.BlockIndex);
                if (_blockCache.ContainsKey(cacheKey)) continue;
                
                var blockSize = _options.BlockSize;
                var blockBuffer = ArrayPool<byte>.Shared.Rent(blockSize);
                try
                {
                    var blockSpan = blockBuffer.AsSpan(0, blockSize);
                    var read = _inner.ReadFile(request.Path, request.BlockIndex * blockSize, blockSpan);
                    
                    if (read == blockSize && _blockCache.Count < _options.MaxCachedBlocks)
                    {
                        var cacheData = new byte[read];
                        blockSpan.CopyTo(cacheData);
                        _blockCache[cacheKey] = new CachedBlock(cacheData, DateTimeOffset.UtcNow);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(blockBuffer);
                }
            }
            catch
            {
                // Best effort only
            }
        }
    }

    public void Dispose()
    {
        _readAheadCts.Cancel();
        _readAheadChannel.Writer.Complete();
        
        try
        {
            _readAheadTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
        
        _dirCache.Clear();
        _entryCache.Clear();
        _blockCache.Clear();
        _readAheadCursor.Clear();
        _inner.Dispose();
        
        _readAheadCts.Dispose();
    }

    /// <summary>
    /// Gets cache statistics for monitoring performance
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var total = hits + misses;
        
        return new CacheStatistics
        {
            CacheHits = hits,
            CacheMisses = misses,
            HitRatio = total > 0 ? (double)hits / total : 0,
            BytesReadFromCache = Interlocked.Read(ref _bytesReadFromCache),
            BytesReadFromDisk = Interlocked.Read(ref _bytesReadFromDisk),
            DirectoryCacheSize = _dirCache.Count,
            EntryCacheSize = _entryCache.Count,
            BlockCacheSize = _blockCache.Count
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "\\";
        var p = path.Replace('/', '\\');
        if (!p.StartsWith("\\")) p = "\\" + p.TrimStart('\\');
        return p;
    }

    private readonly record struct CachedDirectory(RawFsEntry[] Entries, DateTimeOffset CachedAt)
    {
        public bool IsExpired(TimeSpan ttl) => DateTimeOffset.UtcNow - CachedAt > ttl;
    }

    private readonly record struct CachedEntry(RawFsEntry Entry, DateTimeOffset CachedAt)
    {
        public bool IsExpired(TimeSpan ttl) => DateTimeOffset.UtcNow - CachedAt > ttl;
    }

    private readonly record struct CachedBlock(byte[] Data, DateTimeOffset CachedAt)
    {
        public bool IsExpired(TimeSpan ttl) => DateTimeOffset.UtcNow - CachedAt > ttl;
    }

    private readonly record struct ReadAheadRequest(string Path, long BlockIndex);
}

/// <summary>
/// Configuration options for the caching layer
/// </summary>
public sealed class CacheOptions
{
    public static CacheOptions Default => new();
    public static CacheOptions HighPerformance => new()
    {
        BlockSize = 256 * 1024, // 256KB blocks
        MaxCachedBlocks = 10000,
        DirectoryCacheTtl = TimeSpan.FromSeconds(30),
        EntryCacheTtl = TimeSpan.FromSeconds(30),
        BlockCacheTtl = TimeSpan.FromMinutes(5),
        EnableBlockCache = true,
        EnableReadAhead = true,
        ReadAheadBlocks = 8,
        MaxReadAheadFileSize = 100 * 1024 * 1024 // 100MB
    };
    public static CacheOptions Aggressive => new()
    {
        BlockSize = 512 * 1024, // 512KB blocks
        MaxCachedBlocks = 20000,
        DirectoryCacheTtl = TimeSpan.FromMinutes(2),
        EntryCacheTtl = TimeSpan.FromMinutes(2),
        BlockCacheTtl = TimeSpan.FromMinutes(10),
        EnableBlockCache = true,
        EnableReadAhead = true,
        ReadAheadBlocks = 16,
        MaxReadAheadFileSize = 500 * 1024 * 1024 // 500MB
    };

    public int BlockSize { get; init; } = 64 * 1024; // 64KB default
    public int MaxCachedBlocks { get; init; } = 5000;
    public TimeSpan DirectoryCacheTtl { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan EntryCacheTtl { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BlockCacheTtl { get; init; } = TimeSpan.FromMinutes(2);
    public bool EnableBlockCache { get; init; } = true;
    public bool EnableReadAhead { get; init; } = true;
    public int ReadAheadBlocks { get; init; } = 4;
    public long MaxReadAheadFileSize { get; init; } = 50 * 1024 * 1024; // 50MB
}

/// <summary>
/// Cache performance statistics
/// </summary>
public sealed class CacheStatistics
{
    public long CacheHits { get; init; }
    public long CacheMisses { get; init; }
    public double HitRatio { get; init; }
    public long BytesReadFromCache { get; init; }
    public long BytesReadFromDisk { get; init; }
    public int DirectoryCacheSize { get; init; }
    public int EntryCacheSize { get; init; }
    public int BlockCacheSize { get; init; }

    public override string ToString()
    {
        var totalBytes = BytesReadFromCache + BytesReadFromDisk;
        var cacheRatio = totalBytes > 0 ? (double)BytesReadFromCache / totalBytes : 0;
        
        return $"CacheStats[Hits={CacheHits}, Misses={CacheMisses}, HitRatio={HitRatio:P1}, " +
               $"BytesFromCache={BytesReadFromCache / 1024 / 1024}MB, " +
               $"BytesFromDisk={BytesReadFromDisk / 1024 / 1024}MB, " +
               $"CacheEfficiency={cacheRatio:P1}, " +
               $"DirCache={DirectoryCacheSize}, EntryCache={EntryCacheSize}, BlockCache={BlockCacheSize}]";
    }
}
