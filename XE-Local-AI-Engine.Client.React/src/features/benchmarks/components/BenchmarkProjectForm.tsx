import { Button, Checkbox, Group, NumberInput, Select, Stack, Textarea, TextInput } from "@mantine/core";
import { type FormEvent, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import type { BenchmarkEligibleModel, BenchmarkProjectDraft } from "@/features/benchmarks/models/BenchmarkModels";

interface BenchmarkAgentOption {
	id: string;
	name: string;
}

interface BenchmarkProjectFormProps {
	initialValues: BenchmarkProjectDraft;
	agents: BenchmarkAgentOption[];
	models: BenchmarkEligibleModel[];
	disabled?: boolean;
	isSaving?: boolean;
	onSubmit: (draft: BenchmarkProjectDraft) => void;
	onCancel?: () => void;
}

export function BenchmarkProjectForm({
	initialValues,
	agents,
	models,
	disabled = false,
	isSaving,
	onSubmit,
	onCancel,
}: BenchmarkProjectFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState(initialValues);
	const [attempted, setAttempted] = useState(false);
	useEffect(() => setValues(initialValues), [initialValues]);
	const errors = {
		name: values.name.trim() ? undefined : t("pages.benchmarks.validation.name", "Name is required."),
		coreTask: values.coreTask.trim() ? undefined : t("pages.benchmarks.validation.task", "Core task is required."),
		contextTokens: values.contextTokens > 0 ? undefined : t("pages.benchmarks.validation.context", "Context must be positive."),
		agentDefinitionId: values.agentDefinitionId ? undefined : t("pages.benchmarks.validation.agent", "Select an agent."),
		judgeModelName:
			values.judgeEnabled && !values.judgeModelName
				? t("pages.benchmarks.validation.judgeModel", "Select a judge model.")
				: undefined,
	};
	const submit = (event: FormEvent<HTMLFormElement>): void => {
		event.preventDefault();
		setAttempted(true);
		if (Object.values(errors).some(Boolean)) {
			return;
		}
		onSubmit(values);
	};
	return (
		<form onSubmit={submit}>
			<Stack gap="md">
				<TextInput
					label={t("pages.benchmarks.project.name", "Name")}
					required={true}
					disabled={disabled}
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
					disabled={disabled}
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
					disabled={disabled}
					value={values.contextTokens}
					error={attempted ? errors.contextTokens : undefined}
					onChange={(value) => setValues((current) => ({ ...current, contextTokens: Number(value) || 0 }))}
				/>
				<Select
					label={t("pages.benchmarks.project.agent", "Agent")}
					required={true}
					searchable={true}
					disabled={disabled}
					data={agents.map((agent) => ({ value: agent.id, label: agent.name }))}
					value={values.agentDefinitionId || null}
					error={attempted ? errors.agentDefinitionId : undefined}
					onChange={(value) => setValues((current) => ({ ...current, agentDefinitionId: value ?? "" }))}
				/>
				<Checkbox
					label={t("pages.benchmarks.project.judgeEnabled", "Enable automated judge")}
					disabled={disabled}
					checked={values.judgeEnabled}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setValues((current) => ({ ...current, judgeEnabled: checked }));
					}}
				/>
				{values.judgeEnabled ? (
					<Group grow={true} align="flex-start">
						<Select
							label={t("pages.benchmarks.project.judgeModel", "Judge model")}
							required={true}
							searchable={true}
							disabled={disabled}
							data={models.map((model) => ({ value: model.modelName, label: model.modelName }))}
							value={values.judgeModelName}
							error={attempted ? errors.judgeModelName : undefined}
							onChange={(value) => setValues((current) => ({ ...current, judgeModelName: value }))}
						/>
						<NumberInput
							label={t("pages.benchmarks.project.judgeContext", "Judge context tokens")}
							min={1}
							step={1024}
							disabled={disabled}
							value={values.judgeContextTokens ?? ""}
							onChange={(value) => setValues((current) => ({ ...current, judgeContextTokens: Number(value) || null }))}
						/>
					</Group>
				) : null}
				{disabled ? null : (
					<Group justify="flex-end">
						{onCancel ? (
							<Button variant="default" onClick={onCancel}>
								{t("common.cancel", "Cancel")}
							</Button>
						) : null}
						<Button type="submit" loading={isSaving}>
							{t("common.save", "Save")}
						</Button>
					</Group>
				)}
			</Stack>
		</form>
	);
}
