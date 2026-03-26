using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace SqlManager;

internal sealed class ConfigPasswordProtector
{
    private const string KeyPrefixV1 = "smkey:v1";
    private const string CipherPrefixV1 = "smenc:v1";
    private const string KeyPrefixV2 = "smkey:v2";
    private const string CipherPrefixV2 = "smenc:v2";
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 210_000;
    private const int Argon2Iterations = 3;
    private const int Argon2MemorySizeKiB = 65_536;
    private const int Argon2Parallelism = 2;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public void ValidateUnlockPassword(string password)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
        {
            failures.Add("at least 10 characters");
        }

        if (!password.Any(char.IsUpper))
        {
            failures.Add("an uppercase letter");
        }

        if (!password.Any(char.IsLower))
        {
            failures.Add("a lowercase letter");
        }

        if (!password.Any(char.IsDigit))
        {
            failures.Add("a number");
        }

        if (!password.Any(character => !char.IsLetterOrDigit(character)))
        {
            failures.Add("a symbol");
        }

        if (failures.Count > 0)
        {
            throw new UserInputException($"Encryption password must include {string.Join(", ", failures)}.");
        }
    }

    public string CreateEncryptionKey(string password)
    {
        ValidateUnlockPassword(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var descriptor = new KeyDescriptor(KeyDerivationAlgorithm.Argon2Id, Argon2Iterations, Argon2MemorySizeKiB, Argon2Parallelism, salt, Array.Empty<byte>());
        var verifier = DeriveKey(password, descriptor);
        try
        {
            return $"{KeyPrefixV2}:{descriptor.Iterations}:{descriptor.MemorySizeKiB}:{descriptor.Parallelism}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(verifier)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verifier);
        }
    }

    public bool VerifyUnlockPassword(string password, string encryptionKey)
    {
        var descriptor = ParseKeyDescriptor(encryptionKey);
        var candidate = DeriveKey(password, descriptor);
        try
        {
            return CryptographicOperations.FixedTimeEquals(candidate, descriptor.Verifier);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public string EncryptSecret(string plaintext, string password)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var plaintextBytes = Utf8.GetBytes(plaintext);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var descriptor = new CipherPayload(KeyDerivationAlgorithm.Argon2Id, Argon2Iterations, Argon2MemorySizeKiB, Argon2Parallelism, salt, nonce, Array.Empty<byte>(), Array.Empty<byte>());
        var key = DeriveKey(password, descriptor);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
            return $"{CipherPrefixV2}:{descriptor.Iterations}:{descriptor.MemorySizeKiB}:{descriptor.Parallelism}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(ciphertext)}:{Convert.ToBase64String(tag)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string DecryptSecret(string ciphertext, string password)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return string.Empty;
        }

        var payload = ParseCipherPayload(ciphertext);
        var key = DeriveKey(password, payload);
        var plaintext = new byte[payload.Ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext);
            return Utf8.GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            throw new UserInputException($"Stored password payload could not be decrypted: {exception.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static KeyDescriptor ParseKeyDescriptor(string encryptionKey)
    {
        var parts = encryptionKey.Split(':');
        if (parts.Length >= 2 && string.Equals($"{parts[0]}:{parts[1]}", KeyPrefixV2, StringComparison.Ordinal))
        {
            if (parts.Length != 7)
            {
                throw new UserInputException("Config encryption metadata is invalid.");
            }

            return new KeyDescriptor(
                KeyDerivationAlgorithm.Argon2Id,
                ParsePositiveInt(parts[2], "Config encryption metadata has an invalid iteration count."),
                ParsePositiveInt(parts[3], "Config encryption metadata has an invalid memory size."),
                ParsePositiveInt(parts[4], "Config encryption metadata has an invalid parallelism value."),
                Convert.FromBase64String(parts[5]),
                Convert.FromBase64String(parts[6]));
        }

        if (parts.Length == 5 && string.Equals($"{parts[0]}:{parts[1]}", KeyPrefixV1, StringComparison.Ordinal))
        {
            return new KeyDescriptor(
                KeyDerivationAlgorithm.Pbkdf2Sha512,
                ParsePositiveInt(parts[2], "Config encryption metadata has an invalid iteration count."),
                0,
                1,
                Convert.FromBase64String(parts[3]),
                Convert.FromBase64String(parts[4]));
        }

        throw new UserInputException("Config encryption metadata is invalid.");
    }

    private static CipherPayload ParseCipherPayload(string ciphertext)
    {
        var parts = ciphertext.Split(':');
        if (parts.Length >= 2 && string.Equals($"{parts[0]}:{parts[1]}", CipherPrefixV2, StringComparison.Ordinal))
        {
            if (parts.Length != 9)
            {
                throw new UserInputException("Stored password payload is not in the expected encrypted format.");
            }

            return new CipherPayload(
                KeyDerivationAlgorithm.Argon2Id,
                ParsePositiveInt(parts[2], "Stored password payload has an invalid iteration count."),
                ParsePositiveInt(parts[3], "Stored password payload has an invalid memory size."),
                ParsePositiveInt(parts[4], "Stored password payload has an invalid parallelism value."),
                Convert.FromBase64String(parts[5]),
                Convert.FromBase64String(parts[6]),
                Convert.FromBase64String(parts[7]),
                Convert.FromBase64String(parts[8]));
        }

        if (parts.Length == 7 && string.Equals($"{parts[0]}:{parts[1]}", CipherPrefixV1, StringComparison.Ordinal))
        {
            return new CipherPayload(
                KeyDerivationAlgorithm.Pbkdf2Sha512,
                ParsePositiveInt(parts[2], "Stored password payload has an invalid iteration count."),
                0,
                1,
                Convert.FromBase64String(parts[3]),
                Convert.FromBase64String(parts[4]),
                Convert.FromBase64String(parts[5]),
                Convert.FromBase64String(parts[6]));
        }

        throw new UserInputException("Stored password payload is not in the expected encrypted format.");
    }

    private static int ParsePositiveInt(string value, string errorMessage)
        => int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new UserInputException(errorMessage);

    private static byte[] DeriveKey(string password, KeyDescriptor descriptor)
        => DeriveKey(password, descriptor.Algorithm, descriptor.Salt, descriptor.Iterations, descriptor.MemorySizeKiB, descriptor.Parallelism);

    private static byte[] DeriveKey(string password, CipherPayload payload)
        => DeriveKey(password, payload.Algorithm, payload.Salt, payload.Iterations, payload.MemorySizeKiB, payload.Parallelism);

    private static byte[] DeriveKey(string password, KeyDerivationAlgorithm algorithm, byte[] salt, int iterations, int memorySizeKiB, int parallelism)
    {
        return algorithm switch
        {
            KeyDerivationAlgorithm.Pbkdf2Sha512 => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, KeySize),
            KeyDerivationAlgorithm.Argon2Id => DeriveArgon2IdKey(password, salt, iterations, memorySizeKiB, parallelism),
            _ => throw new UserInputException("Unsupported key derivation algorithm.")
        };
    }

    private static byte[] DeriveArgon2IdKey(string password, byte[] salt, int iterations, int memorySizeKiB, int parallelism)
    {
        var passwordBytes = Utf8.GetBytes(password);
        try
        {
            var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                Iterations = iterations,
                MemorySize = memorySizeKiB,
                DegreeOfParallelism = parallelism
            };
            return argon2.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private enum KeyDerivationAlgorithm
    {
        Pbkdf2Sha512,
        Argon2Id
    }

    private sealed record KeyDescriptor(KeyDerivationAlgorithm Algorithm, int Iterations, int MemorySizeKiB, int Parallelism, byte[] Salt, byte[] Verifier);

    private sealed record CipherPayload(KeyDerivationAlgorithm Algorithm, int Iterations, int MemorySizeKiB, int Parallelism, byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}