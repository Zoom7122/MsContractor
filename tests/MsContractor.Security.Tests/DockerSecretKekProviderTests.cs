using System.Security.Cryptography;
using MsContractor.Security;

namespace MsContractor.Security.Tests;

public sealed class DockerSecretKekProviderTests
{
    [Fact]
    public void Constructor_ReadsBase64SecretAndExposesVersion()
    {
        using var directory = new TemporaryDirectory();
        var expectedKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var path = directory.WriteSecret(Convert.ToBase64String(expectedKey) + Environment.NewLine);

        using var provider = new DockerSecretKekProvider(path, 7);

        Assert.Equal(7, provider.KeyVersion);
        Assert.Equal(expectedKey, provider.KeyMaterial.ToArray());
    }

    [Fact]
    public void Constructor_RejectsInvalidBase64()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.WriteSecret("not-base64!");

        Assert.Throws<CryptographicException>(() => new DockerSecretKekProvider(path, 1));
    }

    [Fact]
    public void Constructor_RejectsDecodedKeyWithWrongLength()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.WriteSecret(Convert.ToBase64String(new byte[31]));

        Assert.Throws<CryptographicException>(() => new DockerSecretKekProvider(path, 1));
    }

    [Fact]
    public void Constructor_RejectsMissingSecretFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "missing-secret");

        Assert.Throws<FileNotFoundException>(() => new DockerSecretKekProvider(path, 1));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveKeyVersion()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.WriteSecret(Convert.ToBase64String(new byte[32]));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DockerSecretKekProvider(path, 0));
    }

    [Fact]
    public void KeyMaterial_ThrowsAfterDisposal()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.WriteSecret(Convert.ToBase64String(new byte[32]));
        var provider = new DockerSecretKekProvider(path, 1);

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = provider.KeyMaterial.Length);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("mscontractor-security-").FullName;

        public string WriteSecret(string content)
        {
            var secretPath = System.IO.Path.Combine(Path, "kek.secret");
            File.WriteAllText(secretPath, content);
            return secretPath;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
