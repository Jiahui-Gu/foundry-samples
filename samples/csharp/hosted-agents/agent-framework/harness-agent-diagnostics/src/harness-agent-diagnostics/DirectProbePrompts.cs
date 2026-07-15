namespace HarnessAgentDiagnostics;

internal static class DirectProbePrompts
{
    internal const string Plan =
        """
        Perform only this deterministic probe plan. This probe explicitly prohibits web access, shell access, network access, external files, and all risky actions.
        Call mode_get. Stay in plan mode.
        Create exactly three todos with these exact titles, in this order:
        1. Write probe memory
        2. Compute probe
        3. Verify probe
        Call todos_get_all. End with exactly PLAN_READY.
        """;

    internal const string Execute =
        """
        I explicitly authorize switching from plan mode to execute mode for this deterministic probe only.
        This probe explicitly prohibits web access, shell access, network access, external files, and all risky actions.
        Call mode_set with mode `execute`.
        In Harness file memory, write `experiment.md` with exactly `label=maf-probe; values=3,1,4`.
        Call compute_probe exactly once with label `maf-probe` and values `[3,1,4]`, preserving that argument order.
        Read `experiment.md`. Complete all three todos. Call todos_get_all.
        Return only compact JSON containing mode, sum, sorted values, memory text, and each todo completion state.
        """;

    internal const string Recovery =
        """
        Complete only the remaining deterministic probe steps. This probe explicitly prohibits web access, shell access, network access, external files, and all risky actions.
        Ensure Harness file memory `experiment.md` contains exactly `label=maf-probe; values=3,1,4`.
        If not already completed, call compute_probe exactly once with label `maf-probe` and values `[3,1,4]`, preserving that argument order.
        Read `experiment.md`. Complete every remaining probe todo. Call todos_get_all.
        Return only compact JSON containing mode, sum, sorted values, memory text, and each todo completion state.
        """;
}
