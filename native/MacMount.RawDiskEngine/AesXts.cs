using System.Security.Cryptography;

namespace MacMount.RawDiskEngine;

internal sealed class AesXts : IDisposable
{
    private readonly Aes _aes1;
    private readonly Aes _aes2;
    private readonly ICryptoTransform _encryptor1;
    private readonly ICryptoTransform _decryptor1;
    private readonly ICryptoTransform _encryptor2;

    public AesXts(byte[] key1, byte[] key2)
    {
        _aes1 = Aes.Create();
        _aes1.Mode = CipherMode.ECB;
        _aes1.Padding = PaddingMode.None;
        _aes1.Key = key1;
        _encryptor1 = _aes1.CreateEncryptor();
        _decryptor1 = _aes1.CreateDecryptor();

        _aes2 = Aes.Create();
        _aes2.Mode = CipherMode.ECB;
        _aes2.Padding = PaddingMode.None;
        _aes2.Key = key2;
        _encryptor2 = _aes2.CreateEncryptor();
    }

    public void Decrypt(byte[] plain, int plainOffset, byte[] cipher, int cipherOffset, ulong unitNo)
    {
        var size = cipher.Length - cipherOffset;
        if (size % 16 != 0) throw new ArgumentException("Cipher length must be multiple of 16");

        // Apple XTS-AES: tweak = unitNo as 64-bit LE in the low 8 bytes; upper 8 bytes are zero.
        // This matches Apple's APFS reference implementation and is intentional (not a bug).
        var tweak = new byte[16];
        BitConverter.GetBytes(unitNo).CopyTo(tweak, 0);

        _encryptor2.TransformBlock(tweak, 0, 16, tweak, 0);

        for (var offset = 0; offset < size; offset += 16)
        {
            var cc = new byte[16];
            Xor128(cipher, cipherOffset + offset, tweak, cc);
            var pp = new byte[16];
            _decryptor1.TransformBlock(cc, 0, 16, pp, 0);
            Xor128(pp, 0, tweak, plain, plainOffset + offset);
            MultiplyTweak(tweak);
        }
    }

    public void Dispose()
    {
        _encryptor1.Dispose();
        _decryptor1.Dispose();
        _encryptor2.Dispose();
        _aes1.Dispose();
        _aes2.Dispose();
    }

    private static void Xor128(byte[] a, int aOff, byte[] b, byte[] outBuf)
    {
        outBuf[0] = (byte)(a[aOff + 0] ^ b[0]);
        outBuf[1] = (byte)(a[aOff + 1] ^ b[1]);
        outBuf[2] = (byte)(a[aOff + 2] ^ b[2]);
        outBuf[3] = (byte)(a[aOff + 3] ^ b[3]);
        outBuf[4] = (byte)(a[aOff + 4] ^ b[4]);
        outBuf[5] = (byte)(a[aOff + 5] ^ b[5]);
        outBuf[6] = (byte)(a[aOff + 6] ^ b[6]);
        outBuf[7] = (byte)(a[aOff + 7] ^ b[7]);
        outBuf[8] = (byte)(a[aOff + 8] ^ b[8]);
        outBuf[9] = (byte)(a[aOff + 9] ^ b[9]);
        outBuf[10] = (byte)(a[aOff + 10] ^ b[10]);
        outBuf[11] = (byte)(a[aOff + 11] ^ b[11]);
        outBuf[12] = (byte)(a[aOff + 12] ^ b[12]);
        outBuf[13] = (byte)(a[aOff + 13] ^ b[13]);
        outBuf[14] = (byte)(a[aOff + 14] ^ b[14]);
        outBuf[15] = (byte)(a[aOff + 15] ^ b[15]);
    }

    private static void Xor128(byte[] a, int aOff, byte[] b, byte[] outBuf, int outOff)
    {
        for (var i = 0; i < 16; i++)
            outBuf[outOff + i] = (byte)(a[aOff + i] ^ b[i]);
    }

    private static void MultiplyTweak(byte[] tweak)
    {
        var t0 = BitConverter.ToUInt64(tweak, 0);
        var t1 = BitConverter.ToUInt64(tweak, 8);
        var c1 = (t0 & 0x8000000000000000UL) != 0 ? 1UL : 0UL;
        var c2 = (t1 & 0x8000000000000000UL) != 0 ? 0x87UL : 0UL;
        t0 = (t0 << 1) ^ c2;
        t1 = (t1 << 1) | c1;
        BitConverter.GetBytes(t0).CopyTo(tweak, 0);
        BitConverter.GetBytes(t1).CopyTo(tweak, 8);
    }
}
