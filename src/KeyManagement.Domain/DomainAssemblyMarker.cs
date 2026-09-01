namespace KeyManagement.Domain;

/// <summary>
/// Anchors reflection over this assembly. Exists so tests and composition roots do not have
/// to name an arbitrary domain type that may later move or be renamed.
/// </summary>
public sealed class DomainAssemblyMarker
{
    private DomainAssemblyMarker()
    {
    }
}
