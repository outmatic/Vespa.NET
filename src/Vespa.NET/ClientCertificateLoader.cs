using System.Security.Cryptography.X509Certificates;

namespace Vespa;

internal static class ClientCertificateLoader
{
#if NET9_0_OR_GREATER
    internal static X509Certificate2 LoadPkcs12FromFile(string path, string? password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password);

    internal static X509Certificate2 LoadPkcs12(byte[] data) =>
        X509CertificateLoader.LoadPkcs12(data, null);

    internal static X509Certificate2 LoadCertificateFromFile(string path) =>
        X509CertificateLoader.LoadCertificateFromFile(path);
#else
    internal static X509Certificate2 LoadPkcs12FromFile(string path, string? password) =>
        password is null ? new X509Certificate2(path) : new X509Certificate2(path, password);

    internal static X509Certificate2 LoadPkcs12(byte[] data) =>
        new(data);

    internal static X509Certificate2 LoadCertificateFromFile(string path) =>
        new(path);
#endif
}
