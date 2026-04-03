using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MacMount.RawDiskEngine;

internal static class ApfsConstants
{
    public const ushort KbTagContainerKeybag = 0x0001;
    public const ushort KbTagVolumeKey = 0x0002;
    public const ushort KbTagVolumeUnlockRecords = 0x0003;
    public const ushort KbTagWrappedAesKey = 0x0001;
    public const ushort KbTagUint64 = 0x0002;
    public const ushort KbTagUuid = 0x0003;
    public const ushort KbTagUint32 = 0x0004;
    public const ushort KbTagAesMode = 0x0005;
    public const ushort KbTagProtectedCanary = 0x0006;
    public const ushort KbTagVersion = 0x0007;
    public const ushort KbTagEntropy = 0x0008;
    public const ushort KbTagIterations = 0x0009;
    public const ushort KbTagSalt = 0x000A;
    public const ushort KbTagHmacSha256Key = 0x000B;
    public const ushort KbTagSingleLoop = 0x000C;

    public const uint ApfsFsOneKeyFlag = 0x00000008;

    public const uint AesMode128 = 1;
    public const uint AesMode256 = 2;
}

public sealed class ApfsKeyManager
{
    private readonly IRawBlockDevice _device;
    private readonly ulong _nxBaseOffset;
    private readonly uint _blockSize;

    public ApfsKeyManager(IRawBlockDevice device, ulong nxBaseOffset, uint blockSize)
    {
        _device = device;
        _nxBaseOffset = nxBaseOffset;
        _blockSize = blockSize;
    }

    public async Task<byte[]?> TryUnlockVolumeAsync(Guid volumeUuid, string password, CancellationToken ct = default)
    {
        var keybagData = await LoadContainerKeybagAsync(ct).ConfigureAwait(false);
        if (keybagData is null) return null;

        var kekEntry = FindKeybagEntry(keybagData, ApfsConstants.KbTagVolumeUnlockRecords, volumeUuid);
        if (kekEntry is null) return null;

        var kek = UnwrapKEK(kekEntry, password);
        if (kek is null) return null;

        var vekEntry = FindKeybagEntry(keybagData, ApfsConstants.KbTagVolumeKey, volumeUuid);
        if (vekEntry is null) return null;

        var vek = UnwrapVEK(vekEntry, kek);
        return vek;
    }

    private async Task<byte[]?> LoadContainerKeybagAsync(CancellationToken ct)
    {
        // Read the NX superblock (must be at least large enough for nx_keylocker at 0x4F8 + 16)
        var superblockSize = Math.Max(_blockSize, 1296u); // 0x4F8 + 16 = 0x508 = 1288, round up
        var buf = new byte[superblockSize];
        var read = await _device.ReadAsync((long)_nxBaseOffset, buf, buf.Length, ct).ConfigureAwait(false);
        if (read < 0x508) return null; // not enough data for nx_keylocker field

        // nx_keylocker is a prange_t at offset 0x4F8 in the NX superblock.
        // prange_t { paddr_t pr_start_paddr; uint64_t pr_block_count; }
        // pr_start_paddr is a physical BLOCK address (not byte offset).
        var keybagBlockAddr = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x4F8));
        var keybagBlockCount = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0x500));

        if (keybagBlockAddr == 0 || keybagBlockCount == 0 || keybagBlockCount > 256)
            return null;

        var keybagByteOffset = (long)(keybagBlockAddr * _blockSize);
        var keybagByteSize = (long)(keybagBlockCount * _blockSize);

        if (keybagByteSize > 1024 * 1024)
            return null;

        var keybagBuf = new byte[keybagByteSize];
        await _device.ReadAsync(keybagByteOffset, keybagBuf, keybagBuf.Length, ct).ConfigureAwait(false);

        return keybagBuf;
    }

    private static KeybagEntry? FindKeybagEntry(byte[] keybagData, ushort targetType, Guid targetUuid)
    {
        var offset = 0;
        while (offset + 4 <= keybagData.Length)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(keybagData.AsSpan(offset, 2));
            var len = BinaryPrimitives.ReadUInt16LittleEndian(keybagData.AsSpan(offset + 2, 2));

            if (len < 4 || offset + len > keybagData.Length) break;

            if (tag == targetType)
            {
                var entry = ParseKeybagEntry(keybagData, offset + 4, len - 4);
                if (entry?.VolumeUuid == targetUuid) return entry;
            }

            offset += len;
        }
        return null;
    }

    private static KeybagEntry? ParseKeybagEntry(byte[] data, int offset, int length)
    {
        var entry = new KeybagEntry();
        var end = offset + length;

        var pos = offset;
        while (pos + 4 <= end)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            var len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2, 2));
            pos += 4;

            if (pos + len > end) break;

            switch (tag)
            {
                case ApfsConstants.KbTagUuid:
                    if (len >= 16)
                    {
                        var uuidBytes = new byte[16];
                        Array.Copy(data, pos, uuidBytes, 0, 16);
                        entry.VolumeUuid = new Guid(uuidBytes);
                    }
                    break;
                case ApfsConstants.KbTagWrappedAesKey:
                    if (len > 0)
                    {
                        entry.WrappedKey = new byte[len];
                        Array.Copy(data, pos, entry.WrappedKey, 0, len);
                    }
                    break;
                case ApfsConstants.KbTagIterations:
                    if (len >= 4)
                        entry.Iterations = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                    break;
                case ApfsConstants.KbTagSalt:
                    if (len > 0 && len <= 32)
                    {
                        entry.Salt = new byte[len];
                        Array.Copy(data, pos, entry.Salt, 0, len);
                    }
                    break;
                case ApfsConstants.KbTagAesMode:
                    if (len >= 4)
                        entry.AesMode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                    break;
                case ApfsConstants.KbTagEntropy:
                    if (len > 0 && len <= 32)
                    {
                        entry.Entropy = new byte[len];
                        Array.Copy(data, pos, entry.Entropy, 0, len);
                    }
                    break;
            }

            pos += len;
        }

        return entry.WrappedKey is not null ? entry : null;
    }

    private byte[]? UnwrapKEK(KeybagEntry entry, string password)
    {
        if (entry.Salt is null || entry.Iterations == 0) return null;

        var pwBytes = Encoding.UTF8.GetBytes(password);
        var derivedKey = new byte[32];

        using var pbkdf2 = new Rfc2898DeriveBytes(pwBytes, entry.Salt, (int)entry.Iterations, HashAlgorithmName.SHA256);
        derivedKey = pbkdf2.GetBytes(32);

        if (entry.WrappedKey.Length < 24) return null;

        var unwrappedKey = new byte[entry.WrappedKey.Length - 8];
        var success = Rfc3394KeyUnwrap(unwrappedKey, entry.WrappedKey, entry.WrappedKey.Length - 8, derivedKey, entry.AesMode == ApfsConstants.AesMode256 ? 32 : 16);
        return success ? unwrappedKey : null;
    }

    private byte[]? UnwrapVEK(KeybagEntry entry, byte[] kek)
    {
        if (entry.WrappedKey is null || entry.WrappedKey.Length < 24) return null;

        var unwrappedKey = new byte[entry.WrappedKey.Length - 8];
        var success = Rfc3394KeyUnwrap(unwrappedKey, entry.WrappedKey, entry.WrappedKey.Length - 8, kek, entry.AesMode == ApfsConstants.AesMode256 ? 32 : 16);

        if (!success) return null;

        if (entry.AesMode == ApfsConstants.AesMode128 && unwrappedKey.Length == 16)
        {
            var vek32 = SHA256.HashData(unwrappedKey.Concat(entry.VolumeUuid.ToByteArray()).ToArray());
            return vek32;
        }

        return unwrappedKey;
    }

    private static bool Rfc3394KeyUnwrap(byte[] plain, byte[] crypto, int plainSize, byte[] key, int keyLen)
    {
        var n = plainSize / 8;
        if (n < 1 || n > 6) return false;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var decryptor = aes.CreateDecryptor();

        var a = BinaryPrimitives.ReadUInt64BigEndian(crypto.AsSpan(0, 8));
        var r = new ulong[n];
        for (var i = 0; i < n; i++)
            r[i] = BinaryPrimitives.ReadUInt64LittleEndian(crypto.AsSpan(8 + i * 8, 8));

        var t = 6L * n;
        for (var j = 5; j >= 0; j--)
        {
            for (var i = n - 1; i >= 0; i--)
            {
                var block = new byte[16];
                BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(0, 8), a ^ BinaryPrimitives.ReverseEndianness((ulong)t));
                BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8, 8), r[i]);
                decryptor.TransformBlock(block, 0, 16, block, 0);
                a = BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(0, 8));
                r[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(8, 8));
                t--;
            }
        }

        const ulong expectedIv = 0xA6A6A6A6A6A6A6A6UL;
        if (a != expectedIv) return false;

        for (var i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(plain.AsSpan(i * 8, 8), r[i]);

        return true;
    }

    private sealed class KeybagEntry
    {
        public Guid VolumeUuid { get; set; }
        public byte[]? WrappedKey { get; set; }
        public uint Iterations { get; set; }
        public byte[]? Salt { get; set; }
        public uint AesMode { get; set; }
        public byte[]? Entropy { get; set; }
    }
}
