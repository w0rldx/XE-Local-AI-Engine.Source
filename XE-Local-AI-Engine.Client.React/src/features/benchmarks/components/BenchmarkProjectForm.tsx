import { Button, Checkbox, Divider, Group, NumberInput, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { type FormEvent, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { BenchmarkRubricEditor } from "@/features/benchmarks/components/BenchmarkRubricEditor";
import type {
	BenchmarkEligibleModel,
	BenchmarkProjectDraft,
	BenchmarkRubric,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkRubricIssue, benchmarkRubricLimits } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkRubricPresets } from "@/features/benchmarks/queries/useBenchmarks";

interface BenchmarkAgentOption {
	id: string;
	name: string;
}

interface BenchmarkProjectFormProps {
	initialValues: BenchmarkProjectDraft;
	agents: BenchmarkAgentOption[];
	models: BenchmarkEligibleModel[];
	presets?: BenchmarkRubricPresets;
	/**
	 * The project has runs: its task, agent and context are frozen, but its JUDGE stays editable — changing the judge
	 * re-scores the existing runs instead of invalidating them.
	 */
	frozen?: boolean;
	isSaving?: boolean;
	onSubmit: (draft: BenchmarkProjectDraft) => void;
	onCancel?: () => void;
}

export function BenchmarkProjectForm({
	initialValues,
	agents,
	models,
	presets,
	frozen = false,
	isSaving,
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
