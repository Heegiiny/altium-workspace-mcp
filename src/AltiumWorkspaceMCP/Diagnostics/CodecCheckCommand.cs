using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;

namespace AltiumWorkspaceMCP.Diagnostics;

/// <summary>
/// Checking <see cref="ParameterValueCodec"/> against what Altium itself wrote: for each
/// "displayed value → number" pair from the vault history our own number is computed
/// and compared with the Altium string character by character.
/// </summary>
public static class CodecCheckCommand
{
    public static async Task<int> RunAsync(VaultOptions options)
    {
        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);

        var types = await gateway.GetParameterTypesAsync(CancellationToken.None);

        int totalPairs = 0;
        int totalMatches = 0;
        int totalUnparsed = 0;

        foreach (var type in types.Where(type => !ParameterValueCodec.IsText(type.GUID)).OrderBy(type => type.HRID))
        {
            var parameters = await gateway.GetItemRevisionParametersAsync(
                VaultFilter.Equal("ParameterTypeGUID", type.GUID), limit: 400000,
                cancellationToken: CancellationToken.None);

            var pairs = parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.ParameterValue)
                    && !string.IsNullOrWhiteSpace(parameter.ParameterRealValue))
                .GroupBy(parameter => (parameter.ParameterValue, parameter.ParameterRealValue))
                .Select(group => (Display: group.Key.ParameterValue, Real: group.Key.ParameterRealValue, Count: group.Count()))
                .ToList();

            if (pairs.Count == 0)
            {
                Console.Error.WriteLine($"{type.HRID,-12} no data to check");
                continue;
            }

            int matches = 0;
            int unparsed = 0;
            var mismatches = new List<string>();

            foreach (var (display, real, count) in pairs)
            {
                string? ours = ParameterValueCodec.RealValueFor(display, type.GUID);

                if (ours is null)
                {
                    unparsed += count;
                    mismatches.Add($"not parsed '{display}' (Altium: {real}) ×{count}");
                }
                else if (string.Equals(ours, real, StringComparison.Ordinal))
                {
                    matches += count;
                }
                else
                {
                    mismatches.Add($"mismatch '{display}': ours {ours}, Altium {real} ×{count}");
                }
            }

            int all = pairs.Sum(pair => pair.Count);
            totalPairs += all;
            totalMatches += matches;
            totalUnparsed += unparsed;

            Console.Error.WriteLine($"{type.HRID,-12} values {all,6}, matched {matches,6} "
                + $"({100.0 * matches / all:F2}%), not parsed {unparsed}");

            foreach (string line in mismatches.Take(12))
            {
                Console.Error.WriteLine($"     {line}");
            }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"TOTAL values {totalPairs}, matched {totalMatches} "
            + $"({100.0 * totalMatches / Math.Max(totalPairs, 1):F3}%), not parsed {totalUnparsed}");

        return totalMatches == totalPairs ? 0 : 3;
    }
}
