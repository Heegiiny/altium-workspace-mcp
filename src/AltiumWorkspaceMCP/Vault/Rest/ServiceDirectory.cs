using System.Text;
using System.Xml.Linq;

namespace AltiumWorkspaceMCP.Vault.Rest;

/// <summary>
/// The workspace service directory: the addresses of the search service, the parts catalog and the others
/// by their <c>ServiceKind</c>.
/// </summary>
/// <remarks>
/// The addresses are given by the SOAP call <c>GetServicesEndPoints</c> (<c>POST /servicediscovery/servicediscovery.asmx</c>),
/// no session is needed for it. The result is cached per process: service addresses do not change.
/// </remarks>
public sealed class ServiceDirectory
{
    private const string Action = "http://altium.com/GetServicesEndPoints";

    private readonly Uri _baseUrl;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, Uri>? _services;

    public ServiceDirectory(Uri baseUrl, HttpClient http)
    {
        _baseUrl = baseUrl;
        _http = http;
    }

    /// <summary>The service kinds the server needs.</summary>
    public static class Kinds
    {
        public const string SearchBase = "SEARCHBASE";
        public const string PartCatalogApi = "PARTCATALOG_API";

        /// <summary>The OAuth2/OIDC login server (<c>/unifiedlogin</c>) — for token login.</summary>
        public const string AuthService = "AuthService";

        /// <summary>The long-polling service through which the client gets the authorization code (<c>/actionwait/await</c>).</summary>
        public const string ActionWait = "ActionWait";
    }

    /// <summary>All services: kind → address.</summary>
    public async Task<IReadOnlyDictionary<string, Uri>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (_services is not null)
        {
            return _services;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _services ??= await LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The service address by kind (<see cref="Kinds"/>); a refusal if there is no such service.</summary>
    public async Task<Uri> GetAsync(string kind, CancellationToken cancellationToken)
    {
        var all = await GetAllAsync(cancellationToken);

        return all.TryGetValue(kind, out Uri? url)
            ? url
            : throw new InvalidOperationException(
                $"Service '{kind}' not found in the workspace service directory. Available: {string.Join(", ", all.Keys.Take(20))}.");
    }

    private async Task<IReadOnlyDictionary<string, Uri>> LoadAsync(CancellationToken cancellationToken)
    {
        const string body = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <GetServicesEndPoints xmlns="http://altium.com/">
                  <productName>Altium Designer</productName>
                </GetServicesEndPoints>
              </soap:Body>
            </soap:Envelope>
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, "/servicediscovery/servicediscovery.asmx"))
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("SOAPAction", "\"" + Action + "\"");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The service directory returned HTTP {(int)response.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
        }

        return Parse(text);
    }

    /// <summary>Parses the <c>GetServicesEndPoints</c> response: <c>ServiceKind</c> — <c>ServiceUrl</c> pairs (pure logic).</summary>
    public static IReadOnlyDictionary<string, Uri> Parse(string xml)
    {
        XDocument document = XDocument.Parse(xml);
        var result = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);

        foreach (XElement info in document.Descendants().Where(element => element.Name.LocalName == "EndPointInfo"))
        {
            string? kind = info.Elements().FirstOrDefault(element => element.Name.LocalName == "ServiceKind")?.Value;
            string? url = info.Elements().FirstOrDefault(element => element.Name.LocalName == "ServiceUrl")?.Value;

            if (!string.IsNullOrWhiteSpace(kind) && Uri.TryCreate(url, UriKind.Absolute, out Uri? address))
            {
                result[kind.Trim()] = address;
            }
        }

        return result;
    }
}
