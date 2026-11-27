using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class ClientCertificateLoadingTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();

    public void Dispose() => _directory.Delete(recursive: true);

    [Theory]
    [InlineData("client.pfx", null)]
    [InlineData("client.P12", "test-password")]
    public void CreateClientCertificate_LoadsPkcs12(string fileName, string? password)
    {
        using var key = RSA.Create(2048);
        using var certificate = CreateCertificate(key);
        var path = Path.Combine(_directory.FullName, fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));

        using var loaded = new VespaClientOptions { CertificatePath = path, CertificatePassword = password }
            .CreateClientCertificate();

        Assert.NotNull(loaded);
        Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void CreateClientCertificate_LoadsPemWithOrWithoutPrivateKey()
    {
        using var key = RSA.Create(2048);
        using var certificate = CreateCertificate(key);
        var certPath = Path.Combine(_directory.FullName, "client.pem");
        var keyPath = Path.Combine(_directory.FullName, "key.pem");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());

        using var certOnly = new VespaClientOptions { CertificatePath = certPath }.CreateClientCertificate();
        using var certWithKey = new VespaClientOptions { CertificatePath = certPath, ClientKeyPath = keyPath }
            .CreateClientCertificate();

        Assert.NotNull(certOnly);
        Assert.NotNull(certWithKey);
        Assert.Equal(certificate.Thumbprint, certOnly.Thumbprint);
        Assert.Equal(certificate.Thumbprint, certWithKey.Thumbprint);
        Assert.False(certOnly.HasPrivateKey);
        Assert.True(certWithKey.HasPrivateKey);
    }

    private static X509Certificate2 CreateCertificate(RSA key)
    {
        var request = new CertificateRequest("CN=Vespa test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
    }
}
