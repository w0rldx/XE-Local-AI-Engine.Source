@{
    # PSScriptAnalyzer settings for the release-path PowerShell scripts.
    # Consumed by scripts/lint-release-scripts.sh.
    #
    # Every rule NOT listed under ExcludeRules is active at Error, Warning, and Information
    # severity. The exclusions below are deliberate and each carries its justification — an
    # unexplained exclusion is how a linter quietly stops finding things.

    Severity     = @('Error', 'Warning', 'Information')

    ExcludeRules = @(
        # publish/package-tester-win.ps1 is an interactive operator console script whose entire
        # job is to narrate a release build to a human terminal. Write-Host is the correct call
        # there: Write-Output would pollute the pipeline and be captured by callers, and
        # Write-Information is invisible without -InformationAction. Suppressing this rule keeps
        # the 17 unavoidable hits from burying real findings.
        'PSAvoidUsingWriteHost'
    )
}
