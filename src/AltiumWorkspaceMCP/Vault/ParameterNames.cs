using System.Collections.Concurrent;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Vault parameter names: checking that the requested name exists at all, and a hint
/// of the nearest names if not.
/// </summary>
/// <remarks>
/// A typo in a parameter name silently gives an empty selection or an empty column, and the agent draws
/// the conclusion "there are no such parts". So the name is checked against the vault: existence — by a request by
/// name with limit 1 (fractions of a second, the result is remembered in the process), and the list for hints
/// is read once, only when the name was not found.
/// </remarks>
public sealed class ParameterNames
{
    /// <summary>How many parameter records are read for the hint list.</summary>
    private const int SampleSize = 30000;

    private readonly VaultGateway _gateway;
    private readonly ConcurrentDictionary<string, bool> _exists = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private IReadOnlyList<string>? _sample;

    public ParameterNames(VaultGateway gateway) => _gateway = gateway;

    /// <summary>Whether the vault has at least one parameter with this name.</summary>
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken)
    {
        if (_exists.TryGetValue(name, out bool known))
        {
            return known;
        }

        var found = await _gateway.GetItemRevisionParametersAsync(
            VaultFilter.Equal("HRID", name), limit: 1, cancellationToken: cancellationToken);

        return _exists[name] = found.Count > 0;
    }

    /// <summary>
    /// Refuses if at least one of the names is not in the vault — with the nearest names.
    /// </summary>
    /// <param name="names">The parameter names to check (empty ones are skipped).</param>
    /// <param name="role">What is being checked, for the refusal text: "parameterEquals", "columns".</param>
    /// <param name="extraCandidates">Extra names for hints, for example the columns of the selected parts.</param>
    public async Task EnsureKnownAsync(
        IEnumerable<string> names,
        string role,
        IEnumerable<string>? extraCandidates,
        CancellationToken cancellationToken)
    {
        var unknown = new List<string>();

        foreach (string name in names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await ExistsAsync(name, cancellationToken))
            {
                unknown.Add(name);
            }
        }

        if (unknown.Count == 0)
        {
            return;
        }

        var candidates = (await LoadSampleAsync(cancellationToken))
            .Concat(extraCandidates ?? [])
            .ToList();

        var parts = unknown.Select(name =>
        {
            var near = NameSuggester.Nearest(name, candidates);
            return near.Count > 0
                ? $"'{name}' (perhaps {string.Join(", ", near)})"
                : $"'{name}' (no similar names)";
        });

        throw new InvalidOperationException(
            $"No part in the vault has a parameter with such a name ({role}): {string.Join("; ", parts)}. "
            + "Check the spelling: the parameter names of a specific part are shown by vault_component_detail, "
            + "and the columns of any selection — by vault_table without columns.");
    }

    private async Task<IReadOnlyList<string>> LoadSampleAsync(CancellationToken cancellationToken)
    {
        if (_sample is not null)
        {
            return _sample;
        }

        await _sampleGate.WaitAsync(cancellationToken);
        try
        {
            if (_sample is null)
            {
                var parameters = await _gateway.GetItemRevisionParametersAsync(
                    string.Empty, limit: SampleSize, cancellationToken: cancellationToken);

                _sample = parameters
                    .Select(parameter => parameter.HRID)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return _sample;
        }
        finally
        {
            _sampleGate.Release();
        }
    }
}
