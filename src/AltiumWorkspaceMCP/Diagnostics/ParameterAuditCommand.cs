using AltiumWorkspaceMCP.Configuration;
using AltiumWorkspaceMCP.Vault;
using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Diagnostics;

/// <summary>
/// A study of typed parameters: which types exist, how Altium writes the numeric
/// value for them and how many values lost it.
/// </summary>
public static class ParameterAuditCommand
{
    private const string TextTypeGuid = "4884412E-AAD1-4E69-922A-23C1C75250B1";

    public static async Task<int> RunAsync(VaultOptions options, string[] arguments)
    {
        int samples = arguments.Length > 0 && int.TryParse(arguments[0], out int parsed) ? parsed : 40;

        var endpoints = new VaultEndpoints(options);
        await using var session = new VaultSession(options, endpoints);
        var gateway = new VaultGateway(endpoints, session);

        var types = await gateway.GetParameterTypesAsync(CancellationToken.None);
        Console.Error.WriteLine($"parameter types: {types.Count}");
        foreach (ALU_ParameterType type in types.OrderBy(type => type.HRID))
        {
            Console.Error.WriteLine($"  {type.GUID}  {type.HRID}");
        }

        foreach (ALU_ParameterType type in types.Where(type =>
                     !string.Equals(type.GUID, TextTypeGuid, StringComparison.OrdinalIgnoreCase)))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var parameters = await gateway.GetItemRevisionParametersAsync(
                VaultFilter.Equal("ParameterTypeGUID", type.GUID), limit: 400000,
                cancellationToken: CancellationToken.None);

            var filled = parameters.Where(parameter => !string.IsNullOrWhiteSpace(parameter.ParameterValue)).ToList();
            var withReal = filled.Where(parameter => !string.IsNullOrWhiteSpace(parameter.ParameterRealValue)).ToList();
            var withoutReal = filled.Where(parameter => string.IsNullOrWhiteSpace(parameter.ParameterRealValue)).ToList();

            Console.Error.WriteLine();
            Console.Error.WriteLine($"══ {type.HRID} ({type.GUID}): total {parameters.Count}, with a value {filled.Count}, "
                + $"with a number {withReal.Count}, without a number {withoutReal.Count}  [{clock.ElapsedMilliseconds} ms]");

            foreach (var group in withReal
                         .GroupBy(parameter => parameter.ParameterValue)
                         .OrderByDescending(group => group.Count())
                         .Take(samples))
            {
                var reals = group.Select(parameter => parameter.ParameterRealValue).Distinct().ToList();
                Console.Error.WriteLine($"   «{group.Key}» → {string.Join(" | ", reals)}   ×{group.Count()}");
            }

            // The most unusual records: long ones and with non-digit characters — the parser is checked on them.
            foreach (var group in withReal
                         .Where(parameter => parameter.ParameterValue.Any(char.IsLetter))
                         .GroupBy(parameter => parameter.ParameterValue)
                         .OrderByDescending(group => group.Key.Length)
                         .Take(8))
            {
                Console.Error.WriteLine($"   rare '{group.Key}' → {group.First().ParameterRealValue}");
            }

            foreach (var group in withoutReal.GroupBy(parameter => parameter.ParameterValue).Take(6))
            {
                Console.Error.WriteLine($"   without a number '{group.Key}'  ×{group.Count()}");
            }
        }

        return 0;
    }
}
