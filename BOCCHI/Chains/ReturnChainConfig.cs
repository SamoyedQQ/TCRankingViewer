namespace BOCCHI.Chains;

public struct ReturnChainConfig()
{
    public bool ApproachAetheryte { get; init; } = true;

    // When false, always approach the real aetheryte (needed before an aethernet teleport)
    public bool UseCustomPosition { get; init; } = true;
}
