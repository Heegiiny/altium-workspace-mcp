using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Diagnostic recording of SOAP messages (<c>ALTIUM_SOAP_TRACE=&lt;directory&gt;</c>).
/// </summary>
/// <remarks>
/// Needed to compare the wire format before and after replacing the proxies: the server accepts a request
/// with an extra or rearranged field and silently clears the value, the build will not show this.
/// Each message is a separate file <c>NNNNNN-&lt;service&gt;-&lt;operation&gt;.req.xml</c> (the request
/// as it goes to the network) and <c>.resp.xml</c> (the response). Without the environment variable nothing is
/// done, and in normal work it costs nothing. The session in the request is a real one, so
/// the recording directory must not be published or stored in a repository.
/// </remarks>
internal static class SoapTrace
{
    private static readonly string? Directory = ResolveDirectory();
    private static int _sequence;

    /// <summary>Whether recording is enabled.</summary>
    public static bool Enabled => Directory is not null;

    /// <summary>Attaches the recording to the service client if it is enabled.</summary>
    public static void Attach(ServiceEndpoint endpoint, string service)
    {
        if (Directory is not null)
        {
            endpoint.EndpointBehaviors.Add(new Behavior(service));
        }
    }

    /// <summary>
    /// Records the release script (<c>ExecuteScript.xml</c> from the ZIP package) and the server response:
    /// the script is built by the same proxy classes as SOAP, and is compared the same way.
    /// </summary>
    public static void Script(byte[] package, string? response)
    {
        if (Directory is null)
        {
            return;
        }

        try
        {
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(package), System.IO.Compression.ZipArchiveMode.Read);
            System.IO.Compression.ZipArchiveEntry? entry = archive.GetEntry("ExecuteScript.xml");
            if (entry is null)
            {
                return;
            }

            int number = Interlocked.Increment(ref _sequence);
            using (var reader = new StreamReader(entry.Open()))
            {
                File.WriteAllText(Path.Combine(Directory, $"{number:D6}-script-ExecuteScript.req.xml"), reader.ReadToEnd());
            }

            if (response is not null)
            {
                File.WriteAllText(Path.Combine(Directory, $"{number:D6}-script-ExecuteScript.resp.xml"), response);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Diagnostics must not break the server's work.
        }
    }

    private static string? ResolveDirectory()
    {
        string? path = Environment.GetEnvironmentVariable("ALTIUM_SOAP_TRACE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private sealed class Behavior(string service) : IEndpointBehavior
    {
        public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters)
        {
        }

        public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime) =>
            clientRuntime.ClientMessageInspectors.Add(new Inspector(service));

        public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher)
        {
        }

        public void Validate(ServiceEndpoint endpoint)
        {
        }
    }

    private sealed class Inspector(string service) : IClientMessageInspector
    {
        public object? BeforeSendRequest(ref Message request, IClientChannel channel)
        {
            int number = Interlocked.Increment(ref _sequence);
            string operation = Sanitize(request.Headers.Action ?? "unknown");

            // The message is read once, so we write from a buffered copy and hand the client the copy.
            MessageBuffer buffer = request.CreateBufferedCopy(int.MaxValue);
            Write(number, operation, "req", buffer.CreateMessage());
            request = buffer.CreateMessage();

            return (number, operation);
        }

        public void AfterReceiveReply(ref Message reply, object? correlationState)
        {
            if (correlationState is not (int number, string operation))
            {
                return;
            }

            MessageBuffer buffer = reply.CreateBufferedCopy(int.MaxValue);
            Write(number, operation, "resp", buffer.CreateMessage());
            reply = buffer.CreateMessage();
        }

        private void Write(int number, string operation, string kind, Message message)
        {
            try
            {
                string file = Path.Combine(Directory!, $"{number:D6}-{service}-{operation}.{kind}.xml");
                using var writer = System.Xml.XmlWriter.Create(
                    file, new System.Xml.XmlWriterSettings { Indent = false, Encoding = new System.Text.UTF8Encoding(false) });
                message.WriteMessage(writer);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must not break the server's work.
            }
        }
    }

    /// <summary>A SOAP action like <c>http://altium.com/Foo</c> → <c>Foo</c>, safe for a file name.</summary>
    private static string Sanitize(string action)
    {
        string name = action[(action.LastIndexOf('/') + 1)..];
        return string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '-'));
    }
}
