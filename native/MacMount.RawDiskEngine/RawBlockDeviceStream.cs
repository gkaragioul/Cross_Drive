using System;
using System.IO;
using System.Threading;

namespace MacMount.RawDiskEngine;

internal sealed class RawBlockDeviceStream : Stream
{
    private readonly IRawBlockDevice _device;
    private readonly long _startOffset;
    private readonly long _length;
    private readonly bool _ownsDevice;
    private long _position;
    private bool _disposed;

    public RawBlockDeviceStream(IRawBlockDevice device, long startOffset, long length, bool ownsDevice)
    {
        _device = device;
        _startOffset = Math.Max(0, startOffset);
        _length = Math.Max(0, length);
        _ownsDevice = ownsDevice;
        _position = 0;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
        if (count == 0 || _position >= _length) return 0;

        var capped = (int)Math.Min(count, _length - _position);
        var temp = buffer;
        if (offset != 0)
        {
            temp = new byte[capped];
        }

        var read = _device.ReadAsync(_startOffset + _position, temp, capped, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (read > 0)
        {
            _position += read;
            if (!ReferenceEquals(temp, buffer))
            {
                Buffer.BlockCopy(temp, 0, buffer, offset, read);
            }
        }
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position
        };
        _position = Math.Clamp(target, 0, _length);
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() {}

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing && _ownsDevice)
        {
            _device.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RawBlockDeviceStream));
    }
}
