using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace AltiumWorkspaceMCP.Vault.Rest;

/// <summary>
/// A client of the workspace REST services (search, parts catalog): the same session as the
/// SOAP client, the header <c>Authorization: AFSSessionID …</c>.
/// </summary>
/// <remarks>
/// A 401 response means the session has expired: one re-login and one retry are done —
/// the same as in <see cref="VaultSession.ExecuteAsync{T}"/>. Reads are noted in the dry-run
/// journal, like SOAP reads.
/// </remarks>
public sealed class WorkspaceRestClient
{
    /// <summary>The REPORT method — the search service accepts requests with a body by it (like Altium Designer).</summary>
    public static readonly HttpMethod Report = new("REPORT");

    private readonly VaultSession _session;
    private readonly HttpClient _http;

    public WorkspaceRestClient(VaultSession session, HttpClient http)
    {
        _session = session;
        _http = http;
    }

    /// <summary>Sends a request and returns the response body; the JSON is passed for the body.</summary>
    /// <param name="operation">The operation name for the log and refusal texts.</param>
    public Task<string> SendAsync(
        HttpMethod method,
        Uri url,
        string operation,
        string? jsonBody,
        CancellationToken cancellationToken) =>
        _session.ExecuteAsync(
            async (sessionId, ct) =>
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("AFSSessionID", sessionId);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("AD26");

                if (jsonBody is not null)
                {
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }

                using HttpResponseMessage response = await _http.SendAsync(request, ct);
                string text = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Tells VaultSession that the session must be refreshed.
                    throw new VaultOperationException(operation, VaultSession.UserLoginRequired, "the server returned 401");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new VaultOperationException(
                        operation,
                        ((int)response.StatusCode).ToString(),
                        text.Length > 400 ? text[..400] + " …" : text);
                }

                DryRun.NoteRead<string>(operation, url.AbsolutePath, [$"{(int)response.StatusCode}, {text.Length} characters"]);
                return text;
            },
            cancellationToken);
}
