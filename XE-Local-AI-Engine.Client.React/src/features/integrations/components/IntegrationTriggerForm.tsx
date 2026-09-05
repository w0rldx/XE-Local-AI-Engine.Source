import { Alert, Checkbox, Input, Select, Stack, Switch, Textarea, TextInput } from "@mantine/core";
import { type Ref, useCallback, useEffect, useImperativeHandle, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { IntegrationApprovalWarning } from "@/features/integrations/components/IntegrationApprovalWarning";
import {
	type IntegrationAgentOption,
	type IntegrationSessionPolicy,
	integrationSessionPolicies,
	type IntegrationToolFacts,
	type IntegrationTriggerFormValues,
	integrationTriggerFormSchema,
	integrationTriggerNamePattern,
} from "@/features/integrations/models/IntegrationModels";

/** Imperative handle exposed to the parent (the DialogShell footer's Save button). */
export interface IntegrationTriggerFormHandle {
	/** Runs validation; calls onSubmit only when valid. */
	submit(): void;
}

interface IntegrationTriggerFormProps {
	initialValues: IntegrationTriggerFormValues;
	agents: readonly IntegrationAgentOption[];
	toolsByName: ReadonlyMap<string, IntegrationToolFacts>;
	isEditing: boolean;
	submitError?: string;
	/** Says why `toolsByName` is empty (still loading, or the catalog read failed) so the fail-closed banner is explicable. */
	toolCatalogNotice?: string;
	onSubmit: (values: IntegrationTriggerFormValues) => void;
	onDirtyChange?: (isDirty: boolean) => void;
	ref?: Ref<IntegrationTriggerFormHandle>;
}

function computeIsDirty(current: IntegrationTriggerFormValues, initial: IntegrationTriggerFormValues): boolean {
	return (Object.keys(current) as Array<keyof IntegrationTriggerFormValues>).some((key) => current[key] !== initial[key]);
}

// Create/edit form for an integration trigger. The `name` slug is external-facing — it is the integrator's invoke
// URL — so it is validated LIVE on change rather than on submit like every other field; a caller-visible 404 is the
// cost of getting it wrong. The slug is immutable after creation, so the input is disabled while editing.
export function IntegrationTriggerForm({
	initialValues,
	agents,
	toolsByName,
	isEditing,
	submitError,
	toolCatalogNotice,
	onSubmit,
	onDirtyChange,
	ref,
}: IntegrationTriggerFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<IntegrationTriggerFormValues>(initialValues);
	const [errors, setErrors] = useState<Record<string, string>>({});

	// Report dirty state from an effect so the parent setter is only ever called after commit, never during render.
	useEffect(() => {
		onDirtyChange?.(computeIsDirty(values, initialValues));
	}, [values, initialValues, onDirtyChange]);

	const agentData = useMemo(() => agents.map((agent) => ({ value: agent.id, label: agent.name })), [agents]);

	const selectedAgent = useMemo(
		() => agents.find((agent) => agent.id === values.targetAgentDefinitionId),
		[agents, values.targetAgentDefinitionId],
	);

	// Live slug validation: the message appears the moment the value stops matching, not on submit.
	const liveNameError =
		values.name.length > 0 && !integrationTriggerNamePattern.test(values.name)
			? t(
					"pages.integrations.triggers.validation.nameFormat",
					"Use 2-64 lowercase letters, digits or hyphens, starting with a letter or digit.",
				)
			: undefined;

	const fieldError = useCallback(
		(key: string, fallback: string): string | undefined => {
			const message = errors[key];
			return message === undefined ? undefined : t(`pages.integrations.triggers.validation.${message}`, fallback);
		},
		[errors, t],
	);

	const handleSubmit = useCallback(() => {
		const result = integrationTriggerFormSchema.safeParse(values);
		if (!result.success) {
			const nextErrors: Record<string, string> = {};
			for (const issue of result.error.issues) {
				nextErrors[issue.path.map((segment) => String(segment)).join(".")] = issue.message;
			}
			setErrors(nextErrors);
			return;
		}

		setErrors({});
		onSubmit(values);
	}, [onSubmit, values]);

	useImperativeHandle(ref, () => ({ submit: handleSubmit }), [handleSubmit]);

	return (
		<Stack gap="md" data-testid="integration-trigger-form">
			<TextInput
				label={t("pages.integrations.triggers.form.name.label", "Name")}
				description={t(
					"pages.integrations.triggers.form.name.description",
					"The external identifier callers use to invoke this trigger. It cannot be changed later.",
				)}
				placeholder={t("pages.integrations.triggers.form.name.placeholder", "sensor-hub-ingest")}
				value={values.name}
				required={true}
				disabled={isEditing}
				error={liveNameError ?? fieldError("name", "Use 2-64 lowercase letters, digits or hyphens.")}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, name: value }));
				}}
				data-testid="integration-trigger-form-name"
			/>
			<TextInput
				label={t("pages.integrations.triggers.form.displayName.label", "Display name")}
				placeholder={t("pages.integrations.triggers.form.displayName.placeholder", "Sensor hub ingest")}
				value={values.displayName}
				required={true}
				error={fieldError("displayName", "A display name is required.")}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, displayName: value }));
				}}
				data-testid="integration-trigger-form-display-name"
			/>
			<Textarea
				label={t("pages.integrations.triggers.form.description.label", "Description")}
				placeholder={t("pages.integrations.triggers.form.description.placeholder", "Optional short summary")}
				value={values.description}
				autosize={true}
				minRows={2}
				error={fieldError("description", "The description is longer than the 1024-character limit.")}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, description: value }));
				}}
				data-testid="integration-trigger-form-description"
			/>
			<Switch
				label={t("pages.integrations.triggers.form.enabled.label", "Enabled")}
				description={t(
					"pages.integrations.triggers.form.enabled.description",
					"A disabled trigger rejects every invocation.",
				)}
				checked={values.enabled}
				onChange={(event) => {
					const checked = event.currentTarget.checked;
					setValues((current) => ({ ...current, enabled: checked }));
				}}
				data-testid="integration-trigger-form-enabled"
			/>

			<Select
				label={t("pages.integrations.triggers.form.targetAgent.label", "Target agent")}
				description={t(
					"pages.integrations.triggers.form.targetAgent.description",
					"The saved agent this trigger runs, unattended, for every invocation.",
				)}
				placeholder={t("pages.integrations.triggers.form.targetAgent.placeholder", "Select an agent")}
				data={agentData}
				value={values.targetAgentDefinitionId || null}
				allowDeselect={false}
				required={true}
				error={fieldError("targetAgentDefinitionId", "A target agent is required.")}
				onChange={(value) => {
					if (value === null) {
						return;
					}
					setValues((current) => ({ ...current, targetAgentDefinitionId: value }));
				}}
				data-testid="integration-trigger-form-agent"
			/>

			{toolCatalogNotice ? (
				<Alert color="gray" variant="light" data-testid="integration-trigger-form-catalog-notice">
					{toolCatalogNotice}
				</Alert>
			) : null}

			{selectedAgent ? (
				<IntegrationApprovalWarning
					allowedToolNames={selectedAgent.allowedToolNames}
					toolApprovals={selectedAgent.toolApprovals}
					toolsByName={toolsByName}
				/>
			) : null}

			<Select
				label={t("pages.integrations.triggers.form.sessionPolicy.label", "Session policy")}
				description={t(
					"pages.integrations.triggers.form.sessionPolicy.description",
					"Whether every invocation starts a fresh conversation or the caller keeps one across calls.",
				)}
				data={integrationSessionPolicies.map((policy) => ({
					value: policy,
					label: t(`pages.integrations.triggers.form.sessionPolicy.options.${policy}`, policy),
				}))}
				value={values.sessionPolicy}
				allowDeselect={false}
				onChange={(value) => {
					if (value === null) {
						return;
					}
					setValues((current) => ({ ...current, sessionPolicy: value as IntegrationSessionPolicy }));
				}}
				data-testid="integration-trigger-form-session-policy"
			/>

			<Input.Wrapper
				label={t("pages.integrations.triggers.form.acceptedInputs.label", "Accepted inputs")}
				description={t(
					"pages.integrations.triggers.form.acceptedInputs.description",
					"The payload kinds an invocation may send. At least one is required.",
				)}
				error={fieldError("acceptedInputKinds", "Select at least one accepted input kind.")}
			>
				<Stack gap="xs" mt="xs">
					<Checkbox
						label={t("pages.integrations.triggers.form.acceptedInputs.options.Text", "Text")}
						checked={values.acceptsText}
						onChange={(event) => {
							const checked = event.currentTarget.checked;
							setValues((current) => ({ ...current, acceptsText: checked }));
						}}
						data-testid="integration-trigger-form-accepts-text"
					/>
					<Checkbox
						label={t("pages.integrations.triggers.form.acceptedInputs.options.Json", "JSON")}
						checked={values.acceptsJson}
						onChange={(event) => {
							const checked = event.currentTarget.checked;
							setValues((current) => ({ ...current, acceptsJson: checked }));
						}}
						data-testid="integration-trigger-form-accepts-json"
					/>
				</Stack>
			</Input.Wrapper>

			{submitError ? (
				<Alert color="red" data-testid="integration-trigger-form-error">
					{submitError}
				</Alert>
			) : null}
		</Stack>
	);
}
