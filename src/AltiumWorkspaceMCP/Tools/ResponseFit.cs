using System.Globalization;
using System.Text.Json.Nodes;

namespace AltiumWorkspaceMCP.Tools;

/// <summary>
/// General response-size rule for write tools: the summary in full, per-object details
/// — the first ones that fit, the rest as a count "N more" (<c>resultsOmitted</c>).
/// </summary>
/// <remarks>
/// <para>
/// The rule is applied in one place — to the finished response text (<c>ResponseSizeGuard</c>), so it
/// works the same for a dry run (the report is in <c>result</c>), a real write and the
/// confirmation preview of any write tool; no tool needs to set its own
/// limits.
/// </para>
/// <para>
/// "Summary" — everything that is not a list: counters, notes, plan, token. It is never shortened.
/// "Details" — lists (JSON arrays) at any depth up to <see cref="MaxDepth"/>. If the response does not
/// fit: 1) lists inside records (for example, <c>corrections</c> of each part) are cut
/// to the first <see cref="NestedLimit"/>; 2) whole lists share the free space — short ones
/// stay complete, long ones lose their tail, the primary ones (<see cref="PrimaryLists"/>) get three times more; <see cref="ProtectedLists"/> (relink groups)
/// take their share first, but not more than half. Next to each shortened list <c>X</c>
/// appears <c>XOmitted</c> — how many records are not shown; at the root — <c>responseTrimmed</c> with
/// an explanation for the agent.
/// </para>
/// The length is counted by the same serializer as the client's (<see cref="ResponseJson.Options"/>).
/// </remarks>
public static class ResponseFit
{
    /// <summary>How many records of a list inside a record (corrections etc.) remain when shortening.</summary>
    public const int NestedLimit = 3;

    /// <summary>Object nesting depth at which lists are searched for (result → batch → results).</summary>
    private const int MaxDepth = 3;

    /// <summary>Share of free space that protected lists may take.</summary>
    private const double ProtectedShare = 0.5;

    /// <summary>Summary lists: take their share first and not more than <see cref="ProtectedShare"/>.</summary>
    private static readonly HashSet<string> ProtectedLists = new(StringComparer.Ordinal) { "groups" };

    /// <summary>
    /// Primary details of a response — per-part records (edit results, created copies, preview):
    /// when space is shared they get three times more than other lists (refusals, duplicates, run report).
    /// </summary>
    private static readonly HashSet<string> PrimaryLists = new(StringComparer.Ordinal) { "results", "components", "preview" };

    private const int PrimaryWeight = 3;

    /// <summary>
    /// The first record of a non-primary list is taken even if it does not fit its share, but not larger than this share
    /// of the free space: a sample record of a run (<c>wouldWrite</c>) must not push out the parts.
    /// </summary>
    private const double FirstItemShare = 0.25;

    /// <summary>Length of a value in the response, in JSON characters.</summary>
    private static int Size(JsonNode node) => node.ToJsonString(ResponseJson.Options).Length;

    /// <summary>
    /// Fits the response into <paramref name="maxChars"/>.
    /// </summary>
    /// <param name="json">Tool response text (a JSON object).</param>
    /// <param name="maxChars">Response limit in characters.</param>
    /// <returns>
    /// The original text if it fits; the shortened text; <see langword="null"/> — the response is not a JSON
    /// object, or even the summary without lists does not fit (then a refusal with advice remains).
    /// </returns>
    public static string? Fit(string json, int maxChars)
    {
        if (maxChars <= 0)
        {
            return null;
        }

        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject root)
        {
            return null;
        }

        if (json.Length <= maxChars)
        {
            return json;
        }

        var slots = new List<Slot>();
        Collect(root, string.Empty, 1, slots);

        if (slots.Count == 0)
        {
            return null;
        }

        // Step 1: lists inside records — down to the first few.
        var compacted = CompactNested(slots);

        if (Size(root) <= maxChars)
        {
            Mark(root, slots, maxChars, compacted);
            return Size(root) <= maxChars ? root.ToJsonString(ResponseJson.Options) : null;
        }

        // Step 2: whole lists share the free space.
        foreach (Slot slot in slots)
        {
            slot.Detach();
        }

        int skeleton = Size(root);
        int reserve = 400 + 80 * slots.Count;
        int budget = maxChars - skeleton - reserve;

        if (budget > 0)
        {
            var protectedSlots = slots.Where(slot => slot.IsProtected).ToList();
            var others = slots.Where(slot => !slot.IsProtected).ToList();

            int shareLimit = (int)(budget * ProtectedShare);
            int left = Fill(protectedSlots, shareLimit);
            Fill(others, budget - (shareLimit - left));
        }

        foreach (Slot slot in slots)
        {
            slot.Attach();
        }

        // The reserve estimate may not match the exact length: add until it fits.
        while (true)
        {
            Mark(root, slots, maxChars, compacted);

            int excess = Size(root) - maxChars;

            if (excess <= 0)
            {
                return root.ToJsonString(ResponseJson.Options);
            }

            Slot? biggest = slots
                .Where(slot => slot.Kept > 0)
                .OrderByDescending(slot => slot.KeptSize)
                .FirstOrDefault();

            if (biggest is null)
            {
                return null;
            }

            biggest.DropLast();
        }
    }

    private sealed class Slot
    {
        public Slot(JsonObject parent, string key, string path, JsonArray array)
        {
            Parent = parent;
            Key = key;
            Path = path;
            Array = array;
            Items = array.ToList();
            Sizes = Items.Select(item => (item is null ? 4 : Size(item)) + 1).ToArray();
            Kept = Items.Count;
            Baseline = parent[key + "Omitted"]?.DeepClone();
        }

        public JsonObject Parent { get; }

        public string Key { get; }

        /// <summary>List path for the explanation: "result.results".</summary>
        public string Path { get; }

        public JsonArray Array { get; }

        public List<JsonNode?> Items { get; }

        public int[] Sizes { get; }

        public int Kept { get; set; }

        /// <summary>What <c>XOmitted</c> already held before shortening (a number or text).</summary>
        public JsonNode? Baseline { get; }

        public bool IsProtected => ProtectedLists.Contains(Key);

        public int Weight => PrimaryLists.Contains(Key) ? PrimaryWeight : 1;

        public int TotalSize => Sizes.Sum();

        public int KeptSize => Sizes.Take(Kept).Sum();

        /// <summary>How many records of the list the response would show without shortening (counting those the tool already omitted).</summary>
        public int Total => Items.Count + (Baseline is JsonValue value && value.TryGetValue(out int earlier) ? earlier : 0);

        public void Detach() => Array.Clear();

        /// <summary>Puts the first <see cref="Kept"/> records back into the list.</summary>
        public void Attach()
        {
            Array.Clear();

            foreach (JsonNode? item in Items.Take(Kept))
            {
                Array.Add(item);
            }
        }

        public void DropLast()
        {
            Kept--;
            Array.RemoveAt(Array.Count - 1);
        }
    }

    private static void Collect(JsonObject node, string path, int depth, List<Slot> slots)
    {
        foreach ((string key, JsonNode? value) in node.ToList())
        {
            string here = path.Length == 0 ? key : path + "." + key;

            switch (value)
            {
                case JsonArray { Count: > 0 } array:
                    slots.Add(new Slot(node, key, here, array));
                    break;

                case JsonObject child when depth < MaxDepth:
                    Collect(child, here, depth + 1, slots);
                    break;
            }
        }
    }

    /// <summary>Lists inside records — down to <see cref="NestedLimit"/>; returns which ones were shortened.</summary>
    private static SortedSet<string> CompactNested(IEnumerable<Slot> slots)
    {
        var compacted = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Slot slot in slots.Where(slot => !slot.IsProtected))
        {
            foreach (JsonObject item in slot.Items.OfType<JsonObject>())
            {
                foreach ((string key, JsonNode? value) in item.ToList())
                {
                    if (value is not JsonArray array || array.Count <= NestedLimit)
                    {
                        continue;
                    }

                    int dropped = array.Count - NestedLimit;

                    while (array.Count > NestedLimit)
                    {
                        array.RemoveAt(array.Count - 1);
                    }

                    item[key + "Omitted"] = dropped;
                    compacted.Add(key);
                }
            }
        }

        // Record sizes have changed.
        foreach (Slot slot in slots)
        {
            for (int i = 0; i < slot.Items.Count; i++)
            {
                slot.Sizes[i] = (slot.Items[i] is null ? 4 : Size(slot.Items[i]!)) + 1;
            }
        }

        return compacted;
    }

    /// <summary>
    /// Distributes space by weights: lists go from short ones (weighted) to long ones, each takes as much as it
    /// needs, but not more than its share of what is left. The first record is taken if otherwise the list would be
    /// empty: for a primary list — if it fits at all, for the others — if it is not larger than <see cref="FirstItemShare"/>.
    /// </summary>
    /// <returns>How much space is left.</returns>
    private static int Fill(List<Slot> group, int budget)
    {
        int remaining = budget;
        int firstItemCap = (int)(budget * FirstItemShare);
        var ordered = group.OrderBy(slot => (double)slot.TotalSize / slot.Weight).ToList();
        double weightLeft = ordered.Sum(slot => slot.Weight);

        foreach (Slot slot in ordered)
        {
            int share = (int)(remaining * slot.Weight / weightLeft);
            weightLeft -= slot.Weight;

            int used = 0;
            int kept = 0;

            while (kept < slot.Items.Count && used + slot.Sizes[kept] <= share)
            {
                used += slot.Sizes[kept++];
            }

            if (kept == 0
                && slot.Items.Count > 0
                && slot.Sizes[0] <= remaining
                && (slot.Weight > 1 || slot.Sizes[0] <= firstItemCap))
            {
                used = slot.Sizes[0];
                kept = 1;
            }

            slot.Kept = kept;
            remaining -= used;
        }

        return remaining;
    }

    /// <summary>
    /// Sets <c>XOmitted</c> on shortened lists and an explanatory <c>responseTrimmed</c>; a repeated call
    /// overwrites the earlier marks.
    /// </summary>
    private static void Mark(JsonObject root, List<Slot> slots, int maxChars, SortedSet<string> compacted)
    {
        var parts = new List<string>();

        foreach (Slot slot in slots)
        {
            int trimmed = slot.Items.Count - slot.Kept;

            if (trimmed == 0)
            {
                continue;
            }

            string key = slot.Key + "Omitted";

            slot.Parent[key] = slot.Baseline switch
            {
                JsonValue value when value.TryGetValue(out int earlier) => earlier + trimmed,
                JsonValue value when value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text) =>
                    $"{text} {trimmed} more shortened to the response limit.",
                _ => trimmed,
            };

            parts.Add(string.Create(
                CultureInfo.InvariantCulture, $"{slot.Path} — showing {slot.Kept} of {slot.Total}"));
        }

        if (parts.Count == 0 && compacted.Count == 0)
        {
            return;
        }

        string note = $"Response shortened to the limit of {maxChars} characters (ALTIUM_MAX_RESPONSE_CHARS)."
            + (parts.Count > 0 ? " Lists: " + string.Join("; ", parts) + "." : string.Empty)
            + (compacted.Count > 0
                ? $" Inside records the first {NestedLimit} list items are shown ({string.Join(", ", compacted)}); the rest are in the record's '...Omitted' field."
                : string.Empty)
            + " Counters and the summary were not shortened, the operation ran on all objects of the call: "
            + "only the response was shortened.";

        root["responseTrimmed"] = note;
    }
}
