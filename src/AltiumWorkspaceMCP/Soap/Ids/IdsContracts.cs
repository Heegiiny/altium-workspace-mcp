using System.Runtime.Serialization;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace AltiumWorkspaceMCP.Soap.Ids;

// The login service contracts (IDS) are written by hand and only for the calls the
// server uses: Login (by user name — IdsService, by Windows — IdsNtlmService), GetSessionInfo and Logout.
// These services do not publish a description (WSDL) — metadata is turned off on the server — so the format
// is taken from the observed exchange: SOAP 1.1, the namespace http://tempuri.org/, the headers
// APIVersion and SessionID without a namespace, the body is a wrapper named after the operation.
// Any divergence from what the server accepts is caught by SessionWireFormatTests.

/// <summary>Login parameters: what to do with a session already occupying the account.</summary>
[DataContract(Name = "LoginOptions", Namespace = "http://tempuri.org/")]
public enum LoginOptions
{
    [EnumMember]
    None,

    /// <summary>Close the session that took the slot and log in again.</summary>
    [EnumMember]
    KillExistingSession,

    [EnumMember]
    UseSeparateSession,
}

/// <summary>Result of a login and of a session check. Extra response fields (account and license information) are not read.</summary>
[DataContract(Name = "IDS_LoginResult", Namespace = "http://tempuri.org/")]
public class IDS_LoginResult
{
    [DataMember(EmitDefaultValue = false)]
    public string? SessionId { get; set; }

    [DataMember(Order = 2)]
    public DateTime LastUseDate { get; set; }

    [DataMember(Order = 3)]
    public bool PasswordExpired { get; set; }
}

[MessageContract(WrapperName = "Login", WrapperNamespace = "http://tempuri.org/", IsWrapped = true)]
public class LoginRequest
{
    [MessageHeader(Namespace = "")]
    public string? APIVersion;

    [MessageBodyMember(Namespace = "http://tempuri.org/", Order = 0)]
    public string? Username;

    [MessageBodyMember(Namespace = "http://tempuri.org/", Order = 1)]
    public string? Password;

    [MessageBodyMember(Namespace = "http://tempuri.org/", Order = 2)]
    public bool SecureLogin;

    [MessageBodyMember(Namespace = "http://tempuri.org/", Order = 3)]
    public LoginOptions LoginOptions;
}

[MessageContract(WrapperName = "LoginResponse", WrapperNamespace = "http://tempuri.org/", IsWrapped = true)]
public class LoginResponse
{
    [MessageBodyMember(Namespace = "http://tempuri.org/", Order = 0)]
    public IDS_LoginResult? LoginResult;
}

[MessageContract(WrapperName = "GetSessionInfo", WrapperNamespace = "http://tempuri.org/", IsWrapped = true)]
public class GetSessionInfoRequest
{
    [MessageHeader(Namespace = "")]
    public string? APIVersion;

    [MessageHeader(Namespace = "")]
    public string? SessionID;
}

[MessageContract(WrapperName = "GetSessionInfoResponse", WrapperNamespace = "http://tempuri.org/", IsWrapped = true)]
public class GetSessionInfoResponse
{
    [MessageBodyMember(Namespace = "http://tempuri.org/", Order = 0)]
    public IDS_LoginResult? LoginResult;
}

[MessageContract(WrapperName = "Logout", WrapperNamespace = "http://tempuri.org/", IsWrapped = true)]
public class LogoutRequest
{
    [MessageHeader(Namespace = "")]
    public string? APIVersion;

    [MessageHeader(Namespace = "")]
    public string? SessionID;

    [MessageBodyMember(Namespace = "http://tempuri.org/", Order = 0)]
    public string? InvalidateTypes;
}

[MessageContract(WrapperName = "LogoutResponse", WrapperNamespace = "http://tempuri.org/", IsWrapped = true)]
public class LogoutResponse
{
}

/// <summary>The service for login by user name and password, for checking and closing a session (<c>/ids/IdsService.svc</c>).</summary>
[ServiceContract]
public interface IIdsService
{
    [OperationContract(Action = "Login", ReplyAction = "http://tempuri.org/IIDSService/LoginResponse")]
    Task<LoginResponse> LoginAsync(LoginRequest request);

    [OperationContract(Action = "GetSessionInfo", ReplyAction = "http://tempuri.org/IIDSService/GetSessionInfoResponse")]
    Task<GetSessionInfoResponse> GetSessionInfoAsync(GetSessionInfoRequest request);

    [OperationContract(Action = "Logout", ReplyAction = "http://tempuri.org/IIDSService/LogoutResponse")]
    Task<LogoutResponse> LogoutAsync(LogoutRequest request);
}

/// <summary>The service for login by Windows account (<c>/ids/IdsNtlmService.svc</c>); the name and password are empty.</summary>
[ServiceContract]
public interface IIdsNtlmService
{
    [OperationContract(Action = "Login", ReplyAction = "http://tempuri.org/IIdsNtlmService/LoginResponse")]
    Task<LoginResponse> LoginAsync(LoginRequest request);
}

/// <summary>The client of <see cref="IIdsService"/>: calls with ordinary parameters instead of messages.</summary>
public sealed class IdsServiceClient(Binding binding, EndpointAddress address)
    : ClientBase<IIdsService>(binding, address), IIdsService
{
    public Task<LoginResponse> LoginAsync(LoginRequest request) => Channel.LoginAsync(request);

    public Task<GetSessionInfoResponse> GetSessionInfoAsync(GetSessionInfoRequest request) =>
        Channel.GetSessionInfoAsync(request);

    public Task<LogoutResponse> LogoutAsync(LogoutRequest request) => Channel.LogoutAsync(request);

    public async Task<IDS_LoginResult?> LoginAsync(
        string apiVersion, string username, string password, bool secureLogin, LoginOptions options) =>
        (await LoginAsync(new LoginRequest
        {
            APIVersion = apiVersion,
            Username = username,
            Password = password,
            SecureLogin = secureLogin,
            LoginOptions = options,
        })).LoginResult;

    public async Task<IDS_LoginResult?> GetSessionInfoAsync(string apiVersion, string sessionId) =>
        (await GetSessionInfoAsync(new GetSessionInfoRequest { APIVersion = apiVersion, SessionID = sessionId })).LoginResult;

    public Task<LogoutResponse> LogoutAsync(string apiVersion, string sessionId, string invalidateTypes) =>
        LogoutAsync(new LogoutRequest { APIVersion = apiVersion, SessionID = sessionId, InvalidateTypes = invalidateTypes });
}

/// <summary>The client of <see cref="IIdsNtlmService"/>.</summary>
public sealed class IdsNtlmServiceClient(Binding binding, EndpointAddress address)
    : ClientBase<IIdsNtlmService>(binding, address), IIdsNtlmService
{
    public Task<LoginResponse> LoginAsync(LoginRequest request) => Channel.LoginAsync(request);

    public async Task<IDS_LoginResult?> LoginAsync(
        string apiVersion, string username, string password, bool secureLogin, LoginOptions options) =>
        (await LoginAsync(new LoginRequest
        {
            APIVersion = apiVersion,
            Username = username,
            Password = password,
            SecureLogin = secureLogin,
            LoginOptions = options,
        })).LoginResult;
}
