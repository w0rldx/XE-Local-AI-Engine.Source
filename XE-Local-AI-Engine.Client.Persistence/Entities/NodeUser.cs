namespace XE_Local_AI_Engine.Client.Persistence.Entities;

using Microsoft.AspNetCore.Identity;

public sealed class NodeUser : IdentityUser
{
    public bool SetupCompleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
