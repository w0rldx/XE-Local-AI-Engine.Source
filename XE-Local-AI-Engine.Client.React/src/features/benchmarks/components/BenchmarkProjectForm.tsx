import { Alert, Button, Checkbox, Divider, Group, NumberInput, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { type FormEvent, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { BenchmarkPairwiseEstimateNote } from "@/features/benchmarks/components/BenchmarkPairwiseEstimateNote";
import { BenchmarkRubricEditor } from "@/features/benchmarks/components/BenchmarkRubricEditor";
import type {
	BenchmarkEligibleModel,
	BenchmarkJudgeMode,
	BenchmarkProjectDraft,
	BenchmarkRubric,
} from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkInvocationTimeoutLimits,
	benchmarkJudgeModes,
	benchmarkPromptReserveTokens,
	benchmarkRubricIssue,
	benchmarkRubricLimits,
} from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkRubricPresets } from "@/features/benchmarks/queries/useBenchmarks";

interface BenchmarkAgentOption {
	id: string;
	name: string;
}

interface BenchmarkProjectFormProps {
	initialValues: BenchmarkProjectDraft;
	/** The project the judging-mode estimate is read for. Absent while creating, which has no runs to compare. */
	projectId?: string;
	agents: BenchmarkAgentOption[];
	models: BenchmarkEligibleModel[];
	presets?: BenchmarkRubricPresets;
	/**
	 * The project has runs: its task, agent and context are frozen, but its JUDGE stays editable — changing the judge
	 * re-scores the existing runs instead of invalidating them.
	 */
	frozen?: boolean;
	isSaving?: boolean;
	/** What the node refused the last save with, already carrying its `code`. Cleared by the caller on the next try. */
	saveError?: string | null;
	onSubmit: (draft: BenchmarkProjectDraft) => void;
	onCancel?: () => void;
}

export function BenchmarkProjectForm({
	initialValues,
	projectId,
	agents,
	models,
	presets,
	frozen = false,
	isSaving,
	saveError,
	onSubmit,
	onCancel,
}: BenchmarkProjectFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState(initialValues);
	const [attempted, setAttempted] = useState(false);
	useEffect(() => setValues(initialValues), [initialValues]);
	// A null rubric means "whatever the node's default is"; once judging is on, the operator edits a concrete rubric,
	// so the default preset is materialised as the starting point. Sending it back is identical to omitting it.
	useEffect(() => {
		const fallback = presets?.default;
		if (fallback) {
			setValues((current) => (current.judgeEnabled && current.rubric === null ? { ...current, rubric: fallback } : current));
		}
	}, [presets]);

	const rubricIssue = values.judgeEnabled && values.rubric ? benchmarkRubricIssue(values.rubric) : null;
	const errors = {
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
					? t(
							"pages.benchmarks.validation.reasoningBudget",
							"The reasoning budget must be between 1 and the requested context.",
						)
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
	const submit = (event: FormEvent<HTMLFormElement>): void => {
		event.preventDefault();
		setAttempted(true);
		if (Object.values(errors).some(Boolean)) {
			return;
		}
		onSubmit(values);
	};
	const setRubric = (rubric: BenchmarkRubric): void => setValues((current) => ({ ...current, rubric }));

	return (
		<form onSubmit={submit}>
			<Stack gap="md">
				{/* The node validates the same rules again with the numbers the operator cannot see (the frozen project's
				    own context). Its sentence belongs beside the fields it is about, not only in a toast. */}
				{saveError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-project-save-error">
						{saveError}
					</Alert>
				) : null}
				<TextInput
					label={t("pages.benchmarks.project.name", "Name")}
					required={true}
					disabled={frozen}
					value={values.name}
					error={attempted ? errors.name : undefined}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, name: value }));
					}}
				/>
				<Textarea
					label={t("pages.benchmarks.project.task", "Core task")}
					required={true}
					minRows={5}
					autosize={true}
					disabled={frozen}
					value={values.coreTask}
					error={attempted ? errors.coreTask : undefined}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, coreTask: value }));
					}}
				/>
				<NumberInput
					label={t("pages.benchmarks.project.context", "Requested context tokens")}
					min={1}
					step={1024}
					disabled={frozen}
					value={values.contextTokens}
					error={attempted ? errors.contextTokens : undefined}
					onChange={(value) => setValues((current) => ({ ...current, contextTokens: Number(value) || 0 }))}
				/>
				<NumberInput
					label={t("pages.benchmarks.project.maxOutputTokens", "Max output tokens")}
					description={t(
						"pages.benchmarks.project.maxOutputTokensHint",
						"Leave empty = context-limited. Must be smaller than the requested context.",
					)}
					min={1}
					step={256}
					disabled={frozen}
					value={values.maxOutputTokens ?? ""}
					error={attempted ? errors.maxOutputTokens : undefined}
					onChange={(value) =>
						setValues((current) => ({
							...current,
							// An empty field is "no budget", which must stay null rather than collapse to 0 — 0 would be a
							// budget the node refuses, and the operator never typed it.
							maxOutputTokens: value === "" || value === null ? null : Number(value),
						}))
					}
				/>
				<NumberInput
					label={t("pages.benchmarks.project.reasoningBudget", "Reasoning budget (tokens)")}
					description={t(
						"pages.benchmarks.project.reasoningBudgetHint",
						"Leave empty = as much as the window allows. The context must cover the prompt, this budget AND the max output — a pair that fills the window truncates every run.",
					)}
					min={1}
					step={256}
					disabled={frozen}
					value={values.reasoningBudgetTokens ?? ""}
					error={attempted ? errors.reasoningBudgetTokens : undefined}
					data-testid="benchmark-reasoning-budget"
					onChange={(value) =>
						setValues((current) => ({
							...current,
							reasoningBudgetTokens: value === "" || value === null ? null : Number(value),
						}))
					}
				/>
				<NumberInput
					label={t("pages.benchmarks.project.invocationTimeout", "Generation timeout (s)")}
					description={t(
						"pages.benchmarks.project.invocationTimeoutHint",
						"Seconds; default 900. Raise it for long reasoning runs — a run cancelled by the clock measures the harness, not the model.",
					)}
					min={benchmarkInvocationTimeoutLimits.min}
					max={benchmarkInvocationTimeoutLimits.max}
					step={60}
					disabled={frozen}
					value={values.invocationTimeoutSeconds ?? ""}
					error={attempted ? errors.invocationTimeoutSeconds : undefined}
					onChange={(value) =>
						setValues((current) => ({
							...current,
							invocationTimeoutSeconds: value === "" || value === null ? null : Number(value),
						}))
					}
				/>
				<Select
					label={t("pages.benchmarks.project.agent", "Agent")}
					required={true}
					searchable={true}
					disabled={frozen}
					data={agents.map((agent) => ({ value: agent.id, label: agent.name }))}
					value={values.agentDefinitionId || null}
					error={attempted ? errors.agentDefinitionId : undefined}
					onChange={(value) => setValues((current) => ({ ...current, agentDefinitionId: value ?? "" }))}
				/>
				<Divider
					label={t("pages.benchmarks.project.judgeSection", "Automated judge")}
					labelPosition="left"
					data-testid="benchmark-judge-section"
				/>
				{frozen ? (
					<Text size="xs" c="dimmed">
						{t(
							"pages.benchmarks.project.judgeEditableWhenFrozen",
							"The judge stays editable on a frozen project. Saving a change re-scores every succeeded run.",
						)}
					</Text>
				) : null}
				<Checkbox
					label={t("pages.benchmarks.project.judgeEnabled", "Enable automated judge")}
					checked={values.judgeEnabled}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setValues((current) => ({
							...current,
							judgeEnabled: checked,
							rubric: checked ? (current.rubric ?? presets?.default ?? null) : current.rubric,
						}));
					}}
				/>
				{values.judgeEnabled ? (
					<Stack gap="md">
						{/* Only the judge-policy route carries the mode, and only a frozen project takes that route. That is
						    not a gap: pairwise compares runs against each other, so a project with none has nothing to
						    compare and no mode worth choosing. */}
						{frozen ? (
							<Stack gap={4}>
								<Select
									w={260}
									label={t("pages.benchmarks.project.judgeMode", "Judging mode")}
									description={t(
										"pages.benchmarks.project.judgeModeHelp",
										"Pointwise scores each run against the rubric. Pairwise compares runs against each other and ranks them through one fit.",
									)}
									allowDeselect={false}
									data={benchmarkJudgeModes.map((mode) => ({
										value: mode,
										label: t(`pages.benchmarks.project.judgeModes.${mode}`, mode),
									}))}
									value={values.judgeMode}
									onChange={(value) =>
										setValues((current) => ({
											...current,
											judgeMode: (benchmarkJudgeModes.find((mode) => mode === value) ?? "pointwise") as BenchmarkJudgeMode,
										}))
									}
									data-testid="benchmark-judge-mode"
								/>
								{/* Read BEFORE the save that commits to it: 12 runs is 132 judge calls. */}
								{values.judgeMode === "pairwise" && projectId !== undefined ? (
									<BenchmarkPairwiseEstimateNote projectId={projectId} />
								) : null}
							</Stack>
						) : null}
						<Group grow={true} align="flex-start">
							<Select
								label={t("pages.benchmarks.project.judgeModel", "Judge model")}
								required={true}
								searchable={true}
								data={models.map((model) => ({ value: model.modelName, label: model.modelName }))}
								value={values.judgeModelName}
								error={attempted ? errors.judgeModelName : undefined}
								onChange={(value) => setValues((current) => ({ ...current, judgeModelName: value }))}
							/>
							<NumberInput
								label={t("pages.benchmarks.project.judgeContext", "Judge context tokens")}
								min={1}
								step={1024}
								value={values.judgeContextTokens ?? ""}
								error={attempted ? errors.judgeContextTokens : undefined}
								onChange={(value) => setValues((current) => ({ ...current, judgeContextTokens: Number(value) || null }))}
							/>
						</Group>
						{values.rubric ? (
							<BenchmarkRubricEditor
								rubric={values.rubric}
								presets={presets}
								issue={attempted ? rubricIssue : null}
								onChange={setRubric}
							/>
						) : null}
						<Textarea
							label={t("pages.benchmarks.project.referenceAnswer", "Reference answer (optional)")}
							description={t(
								"pages.benchmarks.project.referenceAnswerHelp",
								"An ideal answer the judge may compare against. Leave empty to judge on the rubric alone.",
							)}
							autosize={true}
							minRows={3}
							value={values.referenceAnswer ?? ""}
							error={attempted ? errors.referenceAnswer : undefined}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setValues((current) => ({ ...current, referenceAnswer: value.length > 0 ? value : null }));
							}}
						/>
					</Stack>
				) : null}
				<Group justify="flex-end">
					{onCancel ? (
						<Button variant="default" onClick={onCancel}>
							{t("common.cancel", "Cancel")}
						</Button>
					) : null}
					<Button type="submit" loading={isSaving}>
						{frozen ? t("pages.benchmarks.project.saveJudge", "Save judge") : t("common.save", "Save")}
					</Button>
				</Group>
			</Stack>
		</form>
	);
}
