import { Alert, Button, Group, Stack, Switch, Textarea, TextInput } from "@mantine/core";
import { IconDeviceFloppy, IconInfoCircle, IconX } from "@tabler/icons-react";
import { type Ref, useCallback, useEffect, useImperativeHandle, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { MarkdownEditorField } from "@/core/ui/components/MarkdownEditorField/MarkdownEditorField";
import { type SkillFormValues, skillFormSchema } from "@/features/skills/models/SkillModels";

// Imperative handle so the host dialog can place Save in its sticky footer (outside the form body) yet still
// trigger the form's own validate-then-submit. The footer button calls submit(); validation stays in the form.
export interface SkillFormHandle {
	submit: () => void;
}

interface SkillFormProps {
	initialValues: SkillFormValues;
	isSubmitting: boolean;
	submitError?: string;
	// When true, the enabled toggle is shown (edit). Create has no enabled field (a new skill always persists
	// enabled by the store default) so the toggle is hidden then.
	showEnabledToggle: boolean;
	onSubmit: (values: SkillFormValues) => void;
	onCancel: () => void;
	/** Imperative handle exposing submit() so a host footer can drive submission. */
	ref?: Ref<SkillFormHandle>;
	/** Hides the form's own Cancel/Save buttons when the host (DialogShell footer) renders them instead. */
	hideActions?: boolean;
	/** Reports whether the current values differ from initialValues so the host can guard close/navigation. */
	onDirtyChange?: (isDirty: boolean) => void;
}

// Per-field error map keyed by the form field. The skill schema is a flat object, so the first Zod issue path
// segment IS the field name — this keeps the error lookup type-safe (no index-signature dotted-access lint).
type SkillFieldErrors = Partial<Record<keyof SkillFormValues, string>>;

// Create/edit form for a node skill (SKILL.md): name + description inputs plus a markdown body editor. Controlled
// Mantine inputs validated with the shared Zod schema on submit. Description + body are sent to the agent's model
// on demand at run time, so a privacy note is shown (plan §10). The enabled toggle is only meaningful on edit.
export function SkillForm({
	initialValues,
	isSubmitting,
	submitError,
	showEnabledToggle,
	onSubmit,
	onCancel,
	ref,
	hideActions = false,
	onDirtyChange,
}: SkillFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<SkillFormValues>(initialValues);
	const [errors, setErrors] = useState<SkillFieldErrors>({});

	// Dirty = current values differ from the initial snapshot. The page keys this component by editor target so
	// initialValues is stable for the lifetime of an editor session — a JSON compare is sufficient.
	const isDirty = useMemo(() => JSON.stringify(values) !== JSON.stringify(initialValues), [values, initialValues]);

	useEffect(() => {
		onDirtyChange?.(isDirty);
	}, [isDirty, onDirtyChange]);

	const handleBodyChange = useCallback((value: string) => {
		setValues((current) => ({ ...current, body: value }));
	}, []);

	const handleSubmit = useCallback(() => {
		const result = skillFormSchema.safeParse(values);
		if (!result.success) {
			const nextErrors: SkillFieldErrors = {};
			for (const issue of result.error.issues) {
				const key = issue.path[0];
				if (typeof key === "string") {
					nextErrors[key as keyof SkillFormValues] = issue.message;
				}
			}
			setErrors(nextErrors);
			return;
		}

		setErrors({});
		onSubmit(result.data);
	}, [onSubmit, values]);

	useImperativeHandle(ref, () => ({ submit: handleSubmit }), [handleSubmit]);

	// Map the name error to a specific message: a blank name and a pattern violation need different copy. An empty
	// name trips BOTH the min(1) and the regex rules, so the message is keyed off the current value (blank → required,
	// otherwise → invalid pattern) rather than the issue order.
	const nameIssue = errors.name;
	const nameError = useMemo(() => {
		if (!nameIssue) {
			return undefined;
		}
		return values.name.trim().length === 0
			? t("pages.skills.form.name.required", "Name is required.")
			: t("pages.skills.form.name.invalid", "Use lowercase letters, digits and dashes only (no leading or trailing dash).");
	}, [nameIssue, values.name, t]);

	return (
		<Stack gap="md" data-testid="skill-form">
			<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="skill-form-privacy-note">
				{t(
					"pages.skills.form.privacyNote",
					"Skills are sent to the agent's model when it loads them. If that model is a cloud provider, the skill content leaves this node.",
				)}
			</Alert>
			<TextInput
				label={t("pages.skills.form.name.label", "Name")}
				description={t("pages.skills.form.name.description", "Lowercase identifier (e.g. invoice-review).")}
				placeholder={t("pages.skills.form.name.placeholder", "invoice-review")}
				value={values.name}
				required={true}
				error={nameError}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, name: value }));
				}}
				data-testid="skill-form-name"
			/>
			<Textarea
				label={t("pages.skills.form.description.label", "Description")}
				description={t(
					"pages.skills.form.description.description",
					"Short summary the model sees to decide whether to load this skill.",
				)}
				placeholder={t("pages.skills.form.description.placeholder", "How to review supplier invoices.")}
				value={values.description}
				required={true}
				autosize={true}
				minRows={2}
				error={errors.description ? t("pages.skills.form.description.required", "Description is required.") : undefined}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, description: value }));
				}}
				data-testid="skill-form-description"
			/>
			<MarkdownEditorField
				label={t("pages.skills.form.body.label", "Body")}
				description={t(
					"pages.skills.form.body.description",
					"The full skill instructions, loaded on demand by the agent's model.",
				)}
				placeholder={t("pages.skills.form.body.placeholder", "# How to review an invoice\n\n1. …")}
				value={values.body}
				required={true}
				minRows={6}
				error={errors.body ? t("pages.skills.form.body.required", "Body is required.") : undefined}
				onChange={handleBodyChange}
				data-testid="skill-form-body"
			/>
			{showEnabledToggle ? (
				<Switch
					label={t("pages.skills.form.enabled.label", "Enabled")}
					description={t(
						"pages.skills.form.enabled.description",
						"Disabled skills are never loaded by an agent, even if still assigned.",
					)}
					checked={values.enabled}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setValues((current) => ({ ...current, enabled: checked }));
					}}
					data-testid="skill-form-enabled"
				/>
			) : null}

			{submitError ? (
				<Alert color="red" data-testid="skill-form-submit-error">
					{submitError}
				</Alert>
			) : null}
			{hideActions ? null : (
				<Group justify="flex-end">
					<Button
						variant="subtle"
						leftSection={<IconX size={16} />}
						onClick={onCancel}
						disabled={isSubmitting}
						data-testid="skill-form-cancel"
					>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						leftSection={<IconDeviceFloppy size={16} />}
						onClick={handleSubmit}
						loading={isSubmitting}
						data-testid="skill-form-submit"
					>
						{t("common.save", "Save")}
					</Button>
				</Group>
			)}
		</Stack>
	);
}
