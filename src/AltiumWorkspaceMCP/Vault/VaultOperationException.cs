using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// The Vault service reports a failure not by an exception but by the MethodResult field in the response.
/// This class turns such a response into an ordinary exception.
/// </summary>
public sealed class VaultOperationException : Exception
{
    public VaultOperationException(string operation, string? faultCode, string? message)
        : base(BuildMessage(operation, faultCode, message))
    {
        Operation = operation;
        FaultCode = faultCode;
    }

    public string Operation { get; }

    public string? FaultCode { get; }

    private static string BuildMessage(string operation, string? faultCode, string? message)
    {
        string detail = string.IsNullOrWhiteSpace(message) ? "the server gave no reason" : message;
        return string.IsNullOrWhiteSpace(faultCode)
            ? $"Vault operation {operation} failed: {detail}"
            : $"Vault operation {operation} failed ({faultCode}): {detail}";
    }

    /// <summary>Checks the read result and throws an exception if the server returned a refusal.</summary>
    public static void ThrowIfFailed(string operation, ALU_Result? result)
    {
        if (result is null)
        {
            throw new VaultOperationException(operation, null, "the server returned an empty result");
        }

        if (!result.Success)
        {
            throw new VaultOperationException(operation, result.FaultCode, result.Message);
        }
    }

    /// <summary>
    /// Checks the change result. Such a response contains an overall success flag and
    /// a separate result for each object: a refusal for one object does not raise the
    /// overall flag, so both levels are checked.
    /// </summary>
    public static void ThrowIfFailed(string operation, ALU_EditResultList? result)
    {
        if (result is null)
        {
            throw new VaultOperationException(operation, null, "the server returned an empty result");
        }

        if (!result.Success)
        {
            throw new VaultOperationException(operation, result.FaultCode, result.Message);
        }

        var failures = (result.Results ?? [])
            .Where(entry => !entry.Success)
            .Select(entry => $"{entry.GUID}: {entry.Message ?? entry.FaultCode ?? "refusal without an explanation"}")
            .ToList();

        if (failures.Count > 0)
        {
            throw new VaultOperationException(
                operation,
                result.Results?.FirstOrDefault(entry => !entry.Success)?.FaultCode,
                string.Join("; ", failures));
        }
    }
}
