namespace AltiumWorkspaceMCP.Vault;

/// <summary>System tag families of the vault.</summary>
public static class VaultTags
{
    /// <summary>
    /// The family in which Altium stores component types — the very ones visible in
    /// Preferences → Data Management → Component Types and by which a part is searched
    /// in the Components panel.
    /// </summary>
    /// <remarks>
    /// The family GUID is a constant of the Altium vault
    /// and is the same for all workspaces.
    /// </remarks>
    public const string ComponentTypeFamilyGuid = "4D9E8D0B-5C9B-43FD-8668-DD6BDD97C109";
}
