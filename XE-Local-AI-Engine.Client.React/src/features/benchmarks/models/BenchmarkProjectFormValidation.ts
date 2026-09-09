import type { TFunction } from "i18next";

import type { BenchmarkProjectDraft, BenchmarkRubricIssue } from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkInvocationTimeoutLimits,
	benchmarkPromptReserveTokens,
	benchmarkRubricLimits,
} from "@/features/benchmarks/models/BenchmarkModels";

/**
 * The project form's field errors, keyed by field. `undefined` = the field is fine; the form only SHOWS them once a
 * submit has been attempted. `rubricIssue` is passed in rather than recomputed because the rubric editor renders the
 * same issue beside the criterion that carries it.
 *
 * The node re-checks every rule with numbers the form cannot see, so this is a courtesy pass, never the authority.
 */
export function validateBenchmarkProjectDraft(
	values: BenchmarkProjectDraft,
	rubricIssue: BenchmarkRubricIssue | null,
	t: TFunction,
) {
	return {
		name: values.name.trim() ? undefined : t("pages.benchmarks.validation.name", "Name is required."),
		coreTask: values.coreTask.trim() ? undefined : t("pages.benchmarks.validation.task", "Core task is required."),
		contextTokens: values.contextTokens > 0 ? undefined : t("pages.benchmarks.validation.context", "Context must be positive."),
		maxOutputTokens:
			values.maxOutputTokens !== null && (values.maxOutputTokens < 1 || values.maxOutputTokens >= values.contextTokens)
				? t("pages.benchmarks.validation.maxOutputTokens", "Max output tokens must be between 1 and the requested context.")
				: undefined,
		// Mirrors `BenchmarkProjectService.ValidateReasoningBudget`: bounded on its own, and additive with the output
		// budget inside one window. A pair that sums past the context is a project whose every run is truncated.
		reasoningBudgetTokens:
			values.reasoningBudgetTokens === null
				? undefined
				: values.reasoningBudgetTokens < 1 || values.reasoningBudgetTokens >= values.contextTokens
					? t("pages.benchmarks.validation.reasoningBudget", "The reasoning budget must be between 1 and the requested context.")
					: values.maxOutputTokens !== null &&
							benchmarkPromptReserveTokens + values.reasoningBudgetTokens + values.maxOutputTokens > values.contextTokens
						? t(
								"pages.benchmarks.validation.reasoningBudgetSum",
								"The reasoning and output budgets must leave at least {{reserve}} tokens of the context for the prompt.",
								{ reserve: benchmarkPromptReserveTokens },
							)
						: undefined,
		invocationTimeoutSeconds:
			values.invocationTimeoutSeconds !== null &&
			(values.invocationTimeoutSeconds < benchmarkInvocationTimeoutLimits.min ||
				values.invocationTimeoutSeconds > benchmarkInvocationTimeoutLimits.max)
				? t("pages.benchmarks.validation.invocationTimeout", "The generation timeout must be between 60 and 7200 seconds.")
				: undefined,
		agentDefinitionId: values.agentDefinitionId ? undefined : t("pages.benchmarks.validation.agent", "Select an agent."),
		judgeModelName:
			values.judgeEnabled && !values.judgeModelName
				? t("pages.benchmarks.validation.judgeModel", "Select a judge model.")
				: undefined,
		judgeContextTokens:
			values.judgeEnabled && (values.judgeContextTokens ?? 0) <= 0
				? t("pages.benchmarks.validation.judgeContext", "Judge context must be positive.")
				: undefined,
		referenceAnswer:
			(values.referenceAnswer?.length ?? 0) > benchmarkRubricLimits.maxReferenceAnswerLength
				? t("pages.benchmarks.validation.referenceAnswer", "The reference answer is too long.")
				: undefined,
		rubric: rubricIssue ? t(`pages.benchmarks.rubric.issues.${rubricIssue.code}`, "The rubric is invalid.") : undefined,
	};
}
