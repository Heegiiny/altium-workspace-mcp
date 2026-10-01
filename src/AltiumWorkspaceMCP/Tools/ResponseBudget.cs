using System.Text.Json;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// Response length budget of a tool: paged listings use it to decide how many rows
/// fit (a response no longer than <c>ALTIUM_MAX_RESPONSE_CHARS</c>).
/// </summary>
/// <remarks>
/// The length is estimated by the same serializer as in the MCP SDK (<see cref="ResponseJson.Options"/>),
/// so the estimate matches what the client will see — including doubled backslashes
/// and character escaping.
/// </remarks>
public sealed class ResponseBudget
{
    private readonly int _limit;
    private int _used;

    /// <param name="maxChars">Response limit in characters.</param>
    /// <param name="reserved">How many characters go to what is not part of the portions: header, columns, hints.</param>
    public ResponseBudget(int maxChars, int reserved = 0)
    {
        _limit = Math.Max(maxChars - reserved, 0);
    }

    /// <summary>How many characters are already taken by accepted portions.</summary>
    public int Used => _used;

    /// <summary>How many characters are still free.</summary>
    public int Remaining => Math.Max(_limit - _used, 0);

    /// <summary>Length of a value in the response, in JSON characters.</summary>
    public static int SizeOf(object? value) =>
        JsonSerializer.Serialize(value, ResponseJson.Options).Length;

    /// <summary>
    /// Accepts a portion if it fits. The first portion is always accepted, otherwise a listing
    /// with one overly long row would never advance.
    /// </summary>
    /// <returns><see langword="false"/> — the portion did not fit, do not accept further ones.</returns>
    public bool TryAdd(object? item)
    {
        // The comma between array elements.
        int size = SizeOf(item) + 1;

        if (_used > 0 && _used + size > _limit)
        {
            return false;
        }

        _used += size;
        return true;
    }

    /// <summary>Takes from the sequence as many items as fit.</summary>
    public List<T> TakeFitting<T>(IEnumerable<T> items)
    {
        var taken = new List<T>();

        foreach (T item in items)
        {
            if (!TryAdd(item))
            {
                break;
            }

            taken.Add(item);
        }

        return taken;
    }
}
