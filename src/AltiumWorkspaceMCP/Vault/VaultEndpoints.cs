using System.ServiceModel;
using System.Xml;
using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Soap.Ids;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Addresses of the workspace services and the WCF binding parameters.
/// The values repeat what Altium Designer itself uses.
/// </summary>
public sealed class VaultEndpoints
{
    /// <summary>The API version the server expects in the header of every call.</summary>
    public const string ApiVersion = "2.0";

    private readonly VaultOptions _options;

    public VaultEndpoints(VaultOptions options)
    {
        _options = options;
        Vault = new Uri(options.BaseUrl, "/vault/VaultService.svc");
        Ids = new Uri(options.BaseUrl, "/ids/IdsService.svc");
        IdsNtlm = new Uri(options.BaseUrl, "/ids/IdsNtlmService.svc");
        ExecuteScript = new Uri(options.BaseUrl, "/vault/ExecuteScript");
    }

    public Uri Vault { get; }

    public Uri Ids { get; }

    public Uri IdsNtlm { get; }

    /// <summary>
    /// Accepting ZIP packages with vault scripts. This is not a SOAP address: the handler
    /// lives next to the service, not under its .svc.
    /// </summary>
    public Uri ExecuteScript { get; }

    public VaultActionServiceClient CreateVaultClient()
    {
        var client = new VaultActionServiceClient(CreateBinding(windowsAuthentication: false), new EndpointAddress(Vault));
        SoapTrace.Attach(client.Endpoint, "vault");
        return client;
    }

    public IdsServiceClient CreateIdsClient()
    {
        var client = new IdsServiceClient(CreateBinding(windowsAuthentication: false), new EndpointAddress(Ids));
        SoapTrace.Attach(client.Endpoint, "ids");
        return client;
    }

    public IdsNtlmServiceClient CreateNtlmClient()
    {
        var client = new IdsNtlmServiceClient(CreateBinding(windowsAuthentication: true), new EndpointAddress(IdsNtlm));
        SoapTrace.Attach(client.Endpoint, "idsntlm");
        return client;
    }

    /// <summary>
    /// The binding is exactly as in Altium: no message size limits, because
    /// parameter and revision selections easily exceed the standard 64 KB.
    /// </summary>
    private BasicHttpBinding CreateBinding(bool windowsAuthentication)
    {
        bool https = string.Equals(_options.BaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        var binding = new BasicHttpBinding
        {
            MaxBufferSize = int.MaxValue,
            MaxReceivedMessageSize = int.MaxValue,
            ReaderQuotas = XmlDictionaryReaderQuotas.Max,
            AllowCookies = true,
            SendTimeout = _options.Timeout,
            ReceiveTimeout = _options.Timeout,
            OpenTimeout = _options.Timeout,
            CloseTimeout = _options.Timeout,
        };

        if (windowsAuthentication)
        {
            binding.Security.Mode = https
                ? BasicHttpSecurityMode.Transport
                : BasicHttpSecurityMode.TransportCredentialOnly;
            binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Windows;
        }
        else if (https)
        {
            binding.Security.Mode = BasicHttpSecurityMode.Transport;
        }

        return binding;
    }
}
