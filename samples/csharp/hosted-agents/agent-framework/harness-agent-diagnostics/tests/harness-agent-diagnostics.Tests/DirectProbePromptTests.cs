using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public sealed class DirectProbePromptTests
{
    [Fact]
    public void PlanPrompt_RequiresDeterministicPlanAndRiskProhibitions()
    {
        string prompt = DirectProbePrompts.Plan;

        Assert.Contains("mode_get", prompt, StringComparison.Ordinal);
        Assert.Contains("Write probe memory", prompt, StringComparison.Ordinal);
        Assert.Contains("Compute probe", prompt, StringComparison.Ordinal);
        Assert.Contains("Verify probe", prompt, StringComparison.Ordinal);
        Assert.Contains("todos_get_all", prompt, StringComparison.Ordinal);
        Assert.Contains("plan mode", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PLAN_READY", prompt, StringComparison.Ordinal);
        AssertRiskProhibitions(prompt);
    }

    [Fact]
    public void ExecutePrompt_RequiresExactMemoryComputeAndAuthorizedModeChange()
    {
        string prompt = DirectProbePrompts.Execute;

        Assert.Contains("explicitly authorize", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mode_set", prompt, StringComparison.Ordinal);
        Assert.Contains("execute", prompt, StringComparison.Ordinal);
        Assert.Contains("experiment.md", prompt, StringComparison.Ordinal);
        Assert.Contains("label=maf-probe; values=3,1,4", prompt, StringComparison.Ordinal);
        Assert.Contains("compute_probe", prompt, StringComparison.Ordinal);
        Assert.Contains("label `maf-probe`", prompt, StringComparison.Ordinal);
        Assert.Contains("[3,1,4]", prompt, StringComparison.Ordinal);
        Assert.Contains("exactly once", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("todos_get_all", prompt, StringComparison.Ordinal);
        Assert.Contains("compact JSON", prompt, StringComparison.Ordinal);
        AssertRiskProhibitions(prompt);
    }

    [Fact]
    public void RecoveryPrompt_IsFixedAndContainsTheDeterministicRemainingWork()
    {
        string prompt = DirectProbePrompts.Recovery;

        Assert.Contains("experiment.md", prompt, StringComparison.Ordinal);
        Assert.Contains("label=maf-probe; values=3,1,4", prompt, StringComparison.Ordinal);
        Assert.Contains("compute_probe", prompt, StringComparison.Ordinal);
        Assert.Contains("[3,1,4]", prompt, StringComparison.Ordinal);
        Assert.Contains("todos_get_all", prompt, StringComparison.Ordinal);
        AssertRiskProhibitions(prompt);
    }

    private static void AssertRiskProhibitions(string prompt)
    {
        Assert.Contains("web", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shell", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("network", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("external files", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("risky actions", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prohibit", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
