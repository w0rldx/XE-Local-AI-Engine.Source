namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Agents.AI;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the Microsoft.Agents.AI Agent Skills contract this node depends on. These assertions are about MAF's
///     behaviour, not ours: they exist because the skills feature was built and live-verified against 1.8.0, the pin
///     later moved to 1.15.0, and nothing in the suite would have noticed a behavioural default changing underneath it.
///     A failure here is not a bug in this repo — it is notice that a package bump changed a security-relevant default
///     and that the call sites in <c>InvocationAgentFactory</c> and <c>SubAgentSpawnService</c> must be re-reasoned.
/// </summary>
public sealed class AgentSkillsProviderContractTests
{
    // Both provider call sites construct with default options, so these three defaults ARE our runtime behaviour:
    // every skill tool arrives approval-gated. That is the correct fail-closed direction, and the reason the
    // sub-agent path has to waive the two read tools explicitly (a spawned child has no approval route).
    [Test]
    public void AgentSkillsProviderOptions_GateEverySkillToolByDefault()
    {
#pragma warning disable MAAI001 // Scoped: the experimental surface is exactly what this test pins.
        var options = new AgentSkillsProviderOptions();

        AssertEx.False(options.DisableLoadSkillApproval,
            "MAF must keep load_skill approval-gated by default; a flip would silently un-gate reading third-party skill content.");
        AssertEx.False(options.DisableReadSkillResourceApproval,
            "MAF must keep read_skill_resource approval-gated by default; it delivers the bulk of imported skill content.");
        AssertEx.False(options.DisableRunSkillScriptApproval,
            "MAF must keep run_skill_script approval-gated by default; this one must never be waived anywhere in this repo.");
#pragma warning restore MAAI001
    }

    // The Agent Skills specification's name rule, asserted against MAF rather than restated. AgentSkillService and
    // AgentDefinitionResolver both delegate here, so this is the shared premise both of them rest on.
    [Test]
    [Arguments("good-name", true)]
    [Arguments("a", true)]
    [Arguments("a-b-c", true)]
    [Arguments("foo--bar", false)]
    [Arguments("-leading", false)]
    [Arguments("trailing-", false)]
    [Arguments("UPPER", false)]
    [Arguments("under_score", false)]
    public void AgentSkillFrontmatter_NameRule_MatchesTheSpecification(string candidate, bool expected)
    {
#pragma warning disable MAAI001
        AssertEx.Equal(expected, AgentSkillFrontmatter.ValidateName(candidate, out _),
            $"The Agent Skills name rule for '{candidate}' changed; AgentSkillService and AgentDefinitionResolver both delegate to it.");
#pragma warning restore MAAI001
    }

    // Guards the specific construction that took down whole turns: a name our validator once accepted but MAF did not.
    [Test]
    public void AgentInlineSkill_RejectsAConsecutiveHyphenName()
    {
        var threw = false;
        try
        {
#pragma warning disable MAAI001
            _ = new AgentInlineSkill("foo--bar", "desc", "body");
#pragma warning restore MAAI001
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        AssertEx.True(threw,
            "A consecutive-hyphen name must still throw at construction — that is why validation delegates to MAF.");
    }
}
