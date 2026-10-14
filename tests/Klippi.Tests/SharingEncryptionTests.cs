using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klippi.Infrastructure.Sharing;
using Xunit;

namespace Klippi.Tests;

public sealed class SharingEncryptionTests
{
    [Fact]
    public void Rfc8188PublishedSingleRecordVectorMatchesBothDirections()
    {
        byte[] key = Base64Url("yqdlZ-tYemfogSmv7Ws5PQ");
        byte[] expected = Base64Url("I1BsxtFttlv3u_Oo94xnmwAAEAAA-NAVub2qFgBEuQKRapoZu-IxkIva3MEB1PD-ly8Thjg");
        byte[] plain = Encoding.UTF8.GetBytes("I am the walrus");
        Assert.Equal(plain, Rfc8188Encryption.Decrypt(expected, key));
        Assert.Equal(expected, Rfc8188Encryption.EncryptWithSalt(plain, key, expected.AsSpan(0, 16), 4096));
    }

    [Fact]
    public void Rfc8188PublishedMultipleRecordVectorHandlesKeyIdAndPadding()
    {
        byte[] key = Base64Url("BO3ZVPxUlnLORbVGMpbT1Q");
        byte[] encrypted = Base64Url("uNCkWiNYzKTnBN9ji3-qWAAAABkCYTHOG8chz_gnvgOqdGYovxyjuqRyJFjEDyoF1Fvkj6hQPdPHI51OEUKEpgz3SsLWIqS_uA");
        Assert.Equal("I am the walrus", Encoding.UTF8.GetString(Rfc8188Encryption.Decrypt(encrypted, key)));
    }

    [Fact]
    public void OfficialFileKiwiSdkFixturesMatchCSharpEncryptionAndDecryption()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/Sharing/sdk-encryption.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        byte[] key = Convert.FromHexString(root.GetProperty("keyHex").GetString()!);
        foreach (var fixture in root.GetProperty("fixtures").EnumerateArray())
        {
            byte[] plain = fixture.GetProperty("encoding").GetString() == "utf8"
                ? Encoding.UTF8.GetBytes(fixture.GetProperty("value").GetString()!)
                : Enumerable.Range(0, fixture.GetProperty("plaintextLength").GetInt32()).Select(i => (byte)((i * 31 + 7) % 256)).ToArray();
            Assert.Equal(fixture.GetProperty("plaintextSha256").GetString(), Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant());
            byte[] encrypted = Convert.FromBase64String(fixture.GetProperty("ciphertextBase64").GetString()!);
            Assert.Equal(plain, Rfc8188Encryption.Decrypt(encrypted, key));
            Assert.Equal(encrypted, Rfc8188Encryption.EncryptWithSalt(plain, key, encrypted.AsSpan(0, 16), 65_536));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65_518)]
    [InlineData(65_519)]
    [InlineData(65_520)]
    [InlineData(131_038)]
    public void RecordBoundariesRoundTripAndAlwaysUseFreshSalt(int length)
    {
        byte[] key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        byte[] plain = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        byte[] first = Rfc8188Encryption.Encrypt(plain, key);
        byte[] second = Rfc8188Encryption.Encrypt(plain, key);
        Assert.NotEqual(Convert.ToHexString(first.AsSpan(0, 16)), Convert.ToHexString(second.AsSpan(0, 16)));
        Assert.Equal(65_536u, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(16, 4)));
        Assert.Equal(plain, Rfc8188Encryption.Decrypt(first, key));
    }

    [Fact]
    public void WrongKeyModifiedTagAndTruncationAreRejected()
    {
        byte[] key = new byte[16];
        byte[] encrypted = Rfc8188Encryption.Encrypt(new byte[65_520], key);
        byte[] wrongKey = key.ToArray();
        wrongKey[0] = 1;
        Assert.ThrowsAny<CryptographicException>(() => Rfc8188Encryption.Decrypt(encrypted, wrongKey));
        Assert.ThrowsAny<CryptographicException>(() => Rfc8188Encryption.Decrypt(encrypted[..^18], key));
        encrypted[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => Rfc8188Encryption.Decrypt(encrypted, key));
    }

    [Fact]
    public void SwappingRecordsIsRejected()
    {
        byte[] key = new byte[16];
        byte[] encrypted = Rfc8188Encryption.Encrypt(new byte[131_038], key);
        byte[] first = encrypted.AsSpan(21, 65_536).ToArray();
        encrypted.AsSpan(21 + 65_536, 65_536).CopyTo(encrypted.AsSpan(21));
        first.CopyTo(encrypted, 21 + 65_536);
        Assert.ThrowsAny<CryptographicException>(() => Rfc8188Encryption.Decrypt(encrypted, key));
    }

    [Fact]
    public void InvalidCryptoParametersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Rfc8188Encryption.Encrypt([1], [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rfc8188Encryption.Encrypt([1], new byte[16], 17));
        Assert.Throws<CryptographicException>(() => Rfc8188Encryption.Decrypt(new byte[20], new byte[16]));
    }

    private static byte[] Base64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}
