using System.Collections.Concurrent;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>A parameter type and a sample value of this type from the vault.</summary>
public sealed record ParameterTypeInfo(string TypeGuid, string? Example);

/// <summary>
/// The type for a parameter that the revision did not have yet.
/// </summary>
/// <remarks>
/// The type decides how Altium treats the value: a text one has no number, for voltage
/// or temperature Altium works exactly with the number. A parameter created as text
/// Altium will show as a string and will not allow comparing and sorting by magnitude. So the type
/// of a new parameter is taken from the parameter with the same name already found in the vault —
/// the very one a template or Altium assigned — and only in its absence text is chosen.
/// A sample value of the same type hints at the format if the value did not parse.
/// </remarks>
public sealed class ParameterTypeResolver
{
    private readonly VaultGateway _gateway;
    private readonly ConcurrentDictionary<string, ParameterTypeInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ParameterTypeResolver(VaultGateway gateway) => _gateway = gateway;

    /// <summary>Types for the names that the revision does not have yet.</summary>
    public Task<IReadOnlyDictionary<string, ParameterTypeInfo>> ResolveNewAsync(
        ALU_ItemRevision revision,
        IEnumerable<string> names,
        CancellationToken cancellationToken) =>
        ResolveAsync(names.Where(name => Find(revision, name) is null), cancellationToken);

    /// <summary>The type for each name: the most frequent among the parameters with this name, otherwise text.</summary>
    public async Task<IReadOnlyDictionary<string, ParameterTypeInfo>> ResolveAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ParameterTypeInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_cache.TryGetValue(name, out ParameterTypeInfo? info))
            {
                var samples = await _gateway.GetItemRevisionParametersAsync(
                    VaultFilter.Equal("HRID", name), limit: 200, cancellationToken: cancellationToken);

                var dominant = samples
                    .Where(parameter => !string.IsNullOrEmpty(parameter.ParameterTypeGUID))
                    .GroupBy(parameter => parameter.ParameterTypeGUID, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .FirstOrDefault();

                info = dominant is null
                    ? new ParameterTypeInfo(ParameterValueCodec.TextTypeGuid, null)
                    : new ParameterTypeInfo(
                        dominant.Key,
                        dominant
                            .Select(parameter => parameter.ParameterValue)
                            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));

                _cache[name] = info;
            }

            result[name] = info;
        }

        return result;
    }

    /// <summary>A revision parameter by name; names are compared case-insensitively, as in Altium.</summary>
    public static ALU_ItemRevisionParameter? Find(ALU_ItemRevision revision, string name) =>
        (revision.RevisionParameters ?? []).FirstOrDefault(parameter =>
            string.Equals(parameter.HRID, name, StringComparison.OrdinalIgnoreCase));
}
