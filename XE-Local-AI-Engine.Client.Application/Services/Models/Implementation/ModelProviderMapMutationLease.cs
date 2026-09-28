namespace XE_Local_AI_Engine.Client.Services.Models.Implementation;

public sealed class ModelProviderMapMutationLease : ModelProviderMapReadLease, IModelProviderMapMutationLease
{
    internal ModelProviderMapMutationLease(IReadOnlyList<string> modelKeys,
        IReadOnlyList<string> mapKeys,
        ModelProviderMapMutationKind kind,
        ModelCoordinationLockLease inner)
        : base(modelKeys, mapKeys, inner)
    {
        Kind = kind;
    }

    public ModelProviderMapMutationKind Kind { get; }
    public override bool IsMutation => true;
}
