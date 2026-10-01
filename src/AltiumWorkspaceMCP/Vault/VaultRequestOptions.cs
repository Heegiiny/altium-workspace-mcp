using AltiumWorkspaceMCP.Soap.Vault;

namespace AltiumWorkspaceMCP.Vault;

/// <summary>
/// Switch strings that the Vault service accepts in the Options field.
/// The names and spelling follow the Altium client and are case-sensitive on the server.
/// </summary>
public static class VaultRequestOptions
{
    public const string IncludeItemRevisions = "IncludeItemRevisions=True";
    public const string IncludeAllItemRevisions = "IncludeAllItemRevisions=True";
    public const string IncludeItemRevisionParameters = "IncludeItemRevisionParameters=True";
    public const string IncludeItemParameters = "IncludeItemParameters=true";
    public const string IncludeFolderParameters = "IncludeFolderParameters=True";
    public const string IncludeLifeCycleStates = "IncludeLifeCycleStates=True";
    public const string IncludeLifeCycleStateChanges = "IncludeLifeCycleStateChanges=True";
    public const string Recursive = "Recursive=True";
    public const string ExcludeDeleted = "ExcludeDeleted=true";
    public const string IncludeDeletedOnly = "IncludeDeletedOnly=true";
    public const string IncludeSystemFolders = "IncludeSystemFolders=true";
    public const string ExcludeUsers = "ExcludeUsers=True";
    public const string ExcludeACLEntries = "ExcludeACLEntries=True";
    public const string GetDirectLinks = "GetDirectLinks=true";

    /// <summary>
    /// Return the parameters of links between items (for a datasheet — FileName, Caption,
    /// FileSize, FileHash). Without the option the server returns links with an empty parameter list
    /// (checked on a live server).
    /// </summary>
    public const string IncludeItemLinkParameters = "IncludeItemLinkParameters=True";

    /// <summary>
    /// Without this switch the server ignores the deletion of parameters when a revision is updated:
    /// parameters absent from the request simply stay as they were.
    /// </summary>
    public const string SupportDeleteRevisionParameters = "SupportDeleteRevisionParameters=True";

    public static _StringList Of(params string[] options)
    {
        var list = new _StringList();
        list.AddRange(options);
        return list;
    }

    public static _StringList None() => new();
}
