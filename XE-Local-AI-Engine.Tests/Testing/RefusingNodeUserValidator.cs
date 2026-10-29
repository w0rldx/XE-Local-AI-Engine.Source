namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.AspNetCore.Identity;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     An Identity user validator that refuses every create or update once <see cref="Refuse" /> is set: the real way
///     to make <c>UserManager.UpdateAsync</c> fail, so a "write not persisted" outcome is reached through Identity itself.
/// </summary>
internal sealed class RefusingNodeUserValidator : IUserValidator<NodeUser>
{
    public bool Refuse { get; set; }

    public Task<IdentityResult> ValidateAsync(UserManager<NodeUser> manager, NodeUser user) =>
        Task.FromResult(Refuse
            ? IdentityResult.Failed(new IdentityError
            {
                Code = "TestRefusal",
                Description = "Refused by the test."
            })
            : IdentityResult.Success);
}
