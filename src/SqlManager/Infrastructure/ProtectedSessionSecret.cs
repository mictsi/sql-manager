using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;

namespace SqlManager;

internal sealed class ProtectedSessionSecret : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private byte[]? _buffer;
    private byte[]? _nonce;
    private LockedMemoryBuffer? _keyBuffer;
    private bool _disposed;

    public bool HasValue => _buffer is { Length: > 0 };

    public void Set(string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Clear();

        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var passwordBytes = Utf8.GetBytes(value);
        var buffer = new byte[passwordBytes.Length + sizeof(int)];

        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, sizeof(int)), passwordBytes.Length);
            passwordBytes.CopyTo(buffer, sizeof(int));
            Protect(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public string? Reveal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_buffer is not { Length: > 0 } buffer)
        {
            return null;
        }

        var workingCopy = Unprotect(buffer);
        try
        {
            var byteCount = BinaryPrimitives.ReadInt32LittleEndian(workingCopy.AsSpan(0, sizeof(int)));
            return byteCount <= 0
                ? string.Empty
                : Utf8.GetString(workingCopy, sizeof(int), byteCount);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(workingCopy);
        }
    }

    public void Clear()
    {
        if (_buffer is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_buffer);
        _buffer = null;
        if (_nonce is not null)
        {
            CryptographicOperations.ZeroMemory(_nonce);
            _nonce = null;
        }

        _keyBuffer?.Dispose();
        _keyBuffer = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _disposed = true;
    }

    private void Protect(byte[] buffer)
    {
        if (OperatingSystem.IsWindows())
        {
            _buffer = ProtectedData.Protect(buffer, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return;
        }

        var keyBytes = RandomNumberGenerator.GetBytes(KeySize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[buffer.Length];
        var tag = new byte[TagSize];

        try
        {
            using var aes = new AesGcm(keyBytes, TagSize);
            aes.Encrypt(nonce, buffer, ciphertext, tag);

            _keyBuffer = LockedMemoryBuffer.Create(keyBytes);
            _nonce = nonce;
            _buffer = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, _buffer, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, _buffer, ciphertext.Length, tag.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private byte[] Unprotect(byte[] buffer)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Unprotect(buffer, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }

        if (_keyBuffer is null || _nonce is not { Length: NonceSize })
        {
            throw new InvalidOperationException("Protected secret is not available.");
        }

        var keyBytes = _keyBuffer.Export();
        var ciphertextLength = buffer.Length - TagSize;
        var ciphertext = new byte[ciphertextLength];
        var tag = new byte[TagSize];
        var plaintext = new byte[ciphertextLength];

        Buffer.BlockCopy(buffer, 0, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(buffer, ciphertextLength, tag, 0, TagSize);

        try
        {
            using var aes = new AesGcm(keyBytes, TagSize);
            aes.Decrypt(_nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private sealed class LockedMemoryBuffer : IDisposable
    {
        private readonly IntPtr _pointer;
        private readonly int _length;
        private bool _disposed;

        private LockedMemoryBuffer(IntPtr pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public static LockedMemoryBuffer Create(byte[] data)
        {
            var pointer = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, pointer, data.Length);
                TryLock(pointer, data.Length);
                TryExcludeFromCoreDumps(pointer, data.Length);
                return new LockedMemoryBuffer(pointer, data.Length);
            }
            catch
            {
                ZeroAndFree(pointer, data.Length);
                throw;
            }
        }

        public byte[] Export()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var data = new byte[_length];
            Marshal.Copy(_pointer, data, 0, _length);
            return data;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            TryUnlock(_pointer, _length);
            ZeroAndFree(_pointer, _length);
            _disposed = true;
        }

        private static void ZeroAndFree(IntPtr pointer, int length)
        {
            if (pointer == IntPtr.Zero)
            {
                return;
            }

            var zeroBuffer = new byte[length];
            Marshal.Copy(zeroBuffer, 0, pointer, length);
            Marshal.FreeHGlobal(pointer);
        }

        private static void TryLock(IntPtr pointer, int length)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                _ = UnixNativeMethods.mlock(pointer, (nuint)length);
            }
        }

        private static void TryUnlock(IntPtr pointer, int length)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                _ = UnixNativeMethods.munlock(pointer, (nuint)length);
            }
        }

        private static void TryExcludeFromCoreDumps(IntPtr pointer, int length)
        {
            if (OperatingSystem.IsLinux())
            {
                _ = UnixNativeMethods.madvise(pointer, (nuint)length, UnixNativeMethods.MADV_DONTDUMP);
            }
        }
    }

    private static class UnixNativeMethods
    {
        public const int MADV_DONTDUMP = 16;

        [DllImport("libc", SetLastError = true)]
        public static extern int mlock(IntPtr addr, nuint len);

        [DllImport("libc", SetLastError = true)]
        public static extern int munlock(IntPtr addr, nuint len);

        [DllImport("libc", SetLastError = true)]
        public static extern int madvise(IntPtr addr, nuint len, int advice);
    }
}