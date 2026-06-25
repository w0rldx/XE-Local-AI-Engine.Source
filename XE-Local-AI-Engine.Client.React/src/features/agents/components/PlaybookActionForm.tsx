import {
	Alert,
	Button,
	Group,
	NumberInput,
	Paper,
	Select,
	Stack,
	Textarea,
	TextInput,
} from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	type PlaybookActionFormValues,
	playbookActionFormSchema,
} from "@/features/agents/models/PlaybookActionModels";

export interface PlaybookActionFormProps {
	initialValues: PlaybookActionFormValues;
	// Hide the Enabled/Disabled state Select when editing a Suggested action — it stays Suggested until Approve, so
	// the operator never sets its state from this form.
	hideStateField?: boolean;
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: PlaybookActionFormValues) => void;
	onCancel: () => void;
}

// Inline add/edit form for a single playbook action. Controlled Mantine inputs validated with the shared Zod
// schema on submit; mirrors AgentDefinitionForm's local-state + on-submit-validate pattern.
export function PlaybookActionForm({
	initialValues,
	hideStateField = false,
	isSubmitting,
	submitError,
	onSubmit,
	onCancel,
}: PlaybookActionFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<PlaybookActionFormValues>(initialValues);
	const [fieldErrors, setFieldErrors] = useState<Partial<Record<keyof PlaybookActionFormValues, string>>>({});

	const handleSubmit = useCallback(() => {
		const result = playbookActionFormSchema.safeParse(values);
		if (!result.success) {
			const nextErrors: Partial<Record<keyof PlaybookActionFormValues, string>> = {};
			for (const issue of result.error.issues) {
				const key = issue.path[0];
				if (typeof key === "string") {
					nextErrors[key as keyof PlaybookActionFormValues] = issue.message;
				}
			}
			setFieldErrors(nextErrors);
			return;
		}

		setFieldErrors({});
		onSubmit(values);
	}, [onSubmit, values]);

	return (
		<Paper withBorder={true} p="sm" data-testid="playbook-action-form">
			<Stack gap="sm">
				<Textarea
					label={t("pages.agents.playbook.form.behavior.label", "Behavior")}
					description={t(
						"pages.agents.playbook.form.behavior.description",
						"Instruction text appended to the agent's system prompt.",
					)}
					placeholder={t("pages.agents.playbook.form.behavior.placeholder", "Always cite your sources…")}
					value={values.behavior}
					required={true}
					autosize={true}
					minRows={2}
					error={fieldErrors.behavior ? t("pages.agents.playbook.form.behavior.required", "Behavior is required") : undefined}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, behavior: value }));
					}}
					data-testid="playbook-form-behavior"
				/>
				<Group grow={true} align="flex-start">
					<TextInput
						label={t("pages.agents.playbook.form.scope.label", "Scope")}
						placeholder={t("pages.agents.playbook.form.scope.placeholder", "Optional topic/tool tag")}
						value={values.scope}
						onChange={(event) => {
							const value = event.currentTarget.value;
							setValues((current) => ({ ...current, scope: value }));
						}}
						data-testid="playbook-form-scope"
					/>
					<NumberInput
						label={t("pages.agents.playbook.form.priority.label", "Priority")}
						description={t("pages.agents.playbook.form.priority.description", "Lower numbers are injected first.")}
						value={values.priority}
						allowDecimal={false}
						onChange={(value) =>
							setValues((current) => ({
								...current,
								priority: typeof value === "number" ? value : Number.parseInt(`${value}`, 10) || 0,
							}))
						}
						data-testid="playbook-form-priority"
					/>
					{hideStateField ? null : (
						<Select
							label={t("pages.agents.playbook.form.state.label", "State")}
							data={[
								{ value: "Enabled", label: t("pages.agents.playbook.state.enabled", "enabled") },
								{ value: "Disabled", label: t("pages.agents.playbook.state.disabled", "disabled") },
							]}
							value={values.state}
							allowDeselect={false}
							onChange={(value) => setValues((current) => ({ ...current, state: value === "Disabled" ? "Disabled" : "Enabled" }))}
							data-testid="playbook-form-state"
						/>
					)}
				</Group>
				<Textarea
					label={t("pages.agents.playbook.form.triggerCondition.label", "Trigger condition")}
					description={t(
						"pages.agents.playbook.form.triggerCondition.description",
						"Optional advisory note describing when this applies (display-only in this phase).",
					)}
					placeholder={t("pages.agents.playbook.form.triggerCondition.placeholder", "When the user asks for…")}
					value={values.triggerCondition}
					autosize={true}
					minRows={1}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, triggerCondition: value }));
					}}
					data-testid="playbook-form-trigger"
				/>
				{submitError ? (
					<Alert color="red" data-testid="playbook-form-submit-error">
						{submitError}
					</Alert>
				) : null}
				<Group justify="flex-end">
					<Button
						variant="subtle"
						size="xs"
						leftSection={<IconX size={14} />}
						onClick={onCancel}
						disabled={isSubmitting}
						data-testid="playbook-form-cancel"
					>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button size="xs" onClick={handleSubmit} loading={isSubmitting} data-testid="playbook-form-submit">
						{t("common.save", "Save")}
					</Button>
				</Group>
			</Stack>
		</Paper>
	);
}
