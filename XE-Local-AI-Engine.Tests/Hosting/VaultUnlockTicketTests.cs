namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Client.Hosting.Vault;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class VaultUnlockTicketTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    [Test]
    public void TryConsume_TheRightValue_SucceedsOnce()
    {
        var value = VaultUnlockTicket.NewValue();
        var ticket = new VaultUnlockTicket(value, Now + VaultUnlockTicket.Lifetime);

        AssertEx.True(ticket.TryConsume(value, Now));
        AssertEx.False(ticket.TryConsume(value, Now), "A consumed ticket must never consume again.");
    }

    [Test]
    public void TryConsume_AtOrAfterExpiry_Fails()
    {
        var value = VaultUnlockTicket.NewValue();
        var ticket = new VaultUnlockTicket(value, Now);

        AssertEx.False(ticket.TryConsume(value, Now));
    }

    [Test]
    public void TryConsume_AWrongValue_FailsAndSpendsTheTicket()
    {
        var value = VaultUnlockTicket.NewValue();
        var ticket = new VaultUnlockTicket(value, Now + VaultUnlockTicket.Lifetime);

        AssertEx.False(ticket.TryConsume(VaultUnlockTicket.NewValue(), Now));
        AssertEx.False(ticket.TryConsume(value, Now), "The first attempt spends the ticket, matching or not.");
    }

    [Test]
    public void NewValue_IsThirtyTwoRandomBytes()
    {
        var first = VaultUnlockTicket.NewValue();

        AssertEx.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(first).Length);
        AssertEx.NotEqual(first, VaultUnlockTicket.NewValue());
    }
}
