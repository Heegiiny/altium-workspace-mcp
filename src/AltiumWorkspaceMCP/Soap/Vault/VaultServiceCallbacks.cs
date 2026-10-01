using System.Runtime.Serialization;

namespace AltiumWorkspaceMCP.Soap.Vault;

// A hand-written addition to the generated proxies (VaultService.cs): rules that the WSDL does not
// express but that the exchange with the server requires. Found by comparing the wire format
// (tools/compare-soap-traces.py): without them the requests and the read objects differ from the earlier ones.

/// <summary>
/// The "created/modified by a workspace guest" flags are declared in the description as optional
/// (nillable), but the server expects an explicit value: an unset flag goes out as <c>false</c>, not
/// as <c>xsi:nil</c> — the earlier client wrote the requests that way too. It applies to the DataContractSerializer
/// (SOAP calls); release scripts are built by XmlSerializer, which has no such callback.
/// </summary>
public partial class ALU_Object
{
    [OnSerializing]
    private void NormalizeWorkspaceGuestFlags(StreamingContext context)
    {
        IsCreatedByWorkspaceGuest = IsCreatedByWorkspaceGuest == true;
        IsLastModifiedByWorkspaceGuest = IsLastModifiedByWorkspaceGuest == true;
    }
}

/// <summary>
/// The <c>IsActive</c> flag is not always present in the server response; an absent one means "active".
/// The value is set before the fields are read, so what the server sent overrides it.
/// </summary>
public partial class ALU_Folder
{
    [OnDeserializing]
    private void DefaultToActive(StreamingContext context) => IsActive = true;
}

/// <inheritdoc cref="ALU_Folder"/>
public partial class ALU_Item
{
    [OnDeserializing]
    private void DefaultToActive(StreamingContext context) => IsActive = true;
}

/// <inheritdoc cref="ALU_Folder"/>
public partial class ALU_ItemRevision
{
    [OnDeserializing]
    private void DefaultToActive(StreamingContext context) => IsActive = true;
}
