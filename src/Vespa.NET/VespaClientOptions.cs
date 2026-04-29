using System.Security.Cryptography.X509Certificates;

namespace Vespa;

/// <summary>
/// Configuration options for VespaClient
/// </summary>
public sealed class VespaClientOptions
{
    /// <summary>
    /// Vespa endpoint URL (e.g., "https://myapp.vespa-cloud.com" or "http://localhost:8080")
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Default namespace for documents
    /// </summary>
    public string DefaultNamespace { get; init; } = "default";

    /// <summary>
    /// Default tenant name for config-server administrative operations.
    /// Used in <c>/application/v2/tenant/{tenant}/...</c> paths.
    /// Can be overridden per-call via the <c>tenant</c> parameter on <see cref="Admin.IAdminOperations"/> methods.
    /// </summary>
    public string Tenant { get; init; } = "default";

    /// <summary>
    /// API key for Vespa Cloud authentication (optional for self-hosted).
    /// Sent as <c>Authorization: Bearer &lt;key&gt;</c> on every request.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Additional HTTP headers sent with every request.
    /// Useful for correlation IDs, tenant headers, or custom auth tokens.
    /// </summary>
    /// <example>
    /// <code>
    /// DefaultRequestHeaders = new Dictionary&lt;string, string&gt;
    /// {
    ///     ["X-Tenant-Id"] = "acme",
    ///     ["X-Request-Source"] = "my-service"
    /// }
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, string>? DefaultRequestHeaders { get; init; }

    // ── mTLS / Vespa Cloud authentication ──────────────────────────────────────

    /// <summary>
    /// Path to the client certificate file.
    /// <list type="bullet">
    ///   <item><description>PFX/P12 file: set <see cref="CertificatePassword"/> if the file is password-protected.</description></item>
    ///   <item><description>PEM certificate: also set <see cref="ClientKeyPath"/> for the private key.</description></item>
    /// </list>
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// Path to the PEM-encoded private key file (used with <see cref="CertificatePath"/> for PEM-based mTLS).
    /// For Vespa Cloud, this is the <c>data-plane-private-key.pem</c> file.
    /// </summary>
    public string? ClientKeyPath { get; init; }

    /// <summary>
    /// Password for the PFX/P12 certificate file (only used when <see cref="CertificatePath"/> points to a PFX file).
    /// </summary>
    public string? CertificatePassword { get; init; }

    /// <summary>
    /// Pre-loaded client certificate. When set, <see cref="CertificatePath"/> and
    /// <see cref="ClientKeyPath"/> are ignored.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>
    /// Creates an <see cref="X509Certificate2"/> from the configured mTLS options, or returns
    /// <see langword="null"/> if mTLS is not configured.
    /// <para>Priority: <see cref="ClientCertificate"/> → PFX file → PEM cert+key pair.</para>
    /// </summary>
    public X509Certificate2? CreateClientCertificate()
    {
        if (ClientCertificate is not null)
            return ClientCertificate;

        if (CertificatePath is null)
            return null;

        // PFX / P12 — bundle contains both cert and private key
        if (CertificatePath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            CertificatePath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12FromFile(
                CertificatePath,
                CertificatePassword);
#else
#pragma warning disable SYSLIB0057
            return CertificatePassword is not null
                ? new X509Certificate2(CertificatePath, CertificatePassword)
                : new X509Certificate2(CertificatePath);
#pragma warning restore SYSLIB0057
#endif

        // PEM cert + separate PEM key (Vespa Cloud style)
        if (ClientKeyPath is not null)
            return X509Certificate2.CreateFromPemFile(CertificatePath, ClientKeyPath);

        // PEM cert only (public cert, no private key — unusual but allowed)
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificateFromFile(CertificatePath);
#else
#pragma warning disable SYSLIB0057
        return new X509Certificate2(CertificatePath);
#pragma warning restore SYSLIB0057
#endif
    }

    /// <summary>
    /// Vespa config server endpoint for administrative operations (schema deploy, application status).
    /// Defaults to the query endpoint with port changed to <c>19071</c>.
    /// Example: <c>"http://localhost:19071"</c>.
    /// </summary>
    public string? ConfigServerEndpoint { get; init; }

    /// <summary>
    /// Default timeout for HTTP requests
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of concurrent connections per server
    /// </summary>
    public int MaxConnectionsPerServer { get; init; } = 100;

    /// <summary>
    /// Enable automatic retry on transient failures
    /// </summary>
    public bool EnableRetry { get; init; } = true;

    /// <summary>
    /// Maximum number of retry attempts
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Enable circuit breaker pattern for fault tolerance
    /// </summary>
    public bool EnableCircuitBreaker { get; init; } = true;

    /// <summary>
    /// Number of failures before circuit breaker opens
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; init; } = 5;

    /// <summary>
    /// Duration to keep circuit breaker open
    /// </summary>
    public TimeSpan CircuitBreakerDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pool HTTP connections for better performance.
    /// When <see langword="false"/>, connections are configured to expire immediately
    /// after use, effectively disabling connection reuse.
    /// </summary>
    public bool UseConnectionPooling { get; init; } = true;

    /// <summary>
    /// Enable compression for requests and responses
    /// </summary>
    public bool UseCompression { get; init; } = true;

    /// <summary>
    /// Connection pool lifetime
    /// </summary>
    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Connection pool idle timeout
    /// </summary>
    public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initial HTTP/2 stream window size in bytes.
    /// When <c>null</c> (default), the <see cref="System.Net.Http.SocketsHttpHandler"/> default is used.
    /// </summary>
    public int? InitialHttp2StreamWindowSize { get; init; }

    /// <summary>
    /// Maximum time allowed to establish a TCP connection.
    /// When <c>null</c> (default), the <see cref="System.Net.Http.SocketsHttpHandler"/> default is used.
    /// </summary>
    public TimeSpan? ConnectTimeout { get; init; }

    /// <summary>
    /// Validate options
    /// </summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint);

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
            throw new ArgumentException("Endpoint must be a valid URL", nameof(Endpoint));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConnectionsPerServer);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetryAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CircuitBreakerFailureThreshold);
        ThrowIfTimeSpanZeroOrNegative(CircuitBreakerDuration, nameof(CircuitBreakerDuration));
        ThrowIfTimeSpanZeroOrNegative(Timeout, nameof(Timeout));
        ThrowIfTimeSpanZeroOrNegative(PooledConnectionLifetime, nameof(PooledConnectionLifetime));
        ThrowIfTimeSpanZeroOrNegative(PooledConnectionIdleTimeout, nameof(PooledConnectionIdleTimeout));

        if (InitialHttp2StreamWindowSize.HasValue)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(InitialHttp2StreamWindowSize.Value);
        if (ConnectTimeout.HasValue)
            ThrowIfTimeSpanZeroOrNegative(ConnectTimeout.Value, nameof(ConnectTimeout));
    }

    private static void ThrowIfTimeSpanZeroOrNegative(TimeSpan value, string paramName)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(paramName);
    }
}
