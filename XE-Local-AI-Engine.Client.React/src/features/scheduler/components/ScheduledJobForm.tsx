import { Alert, NumberInput, Select, Stack, Switch, Textarea, TextInput } from "@mantine/core";
import { type Ref, useCallback, useEffect, useImperativeHandle, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ScheduledJobScheduleFields } from "@/features/scheduler/components/ScheduledJobScheduleFields";
import {
	type ScheduledJobFormValues,
	type ScheduledJobTemplate,
	type ScheduleKind,
	type SchedulerMisfirePolicy,
	scheduledJobFormSchema,
	scheduleKinds,
	schedulerMisfirePolicies,
} from "@/features/scheduler/models/SchedulerModels";

/** Imperative handle exposed to the parent (e.g. DialogShell footer Save button). */
export interface ScheduledJobFormHandle {
	/** Runs Zod validation; calls onSubmit only when valid. */
	submit(): void;
}

interface ScheduledJobFormProps {
	initialValues: ScheduledJobFormValues;
	templates: readonly ScheduledJobTemplate[];
	isEditing: boolean;
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: ScheduledJobFormValues) => void;
	onCancel: () => void;
	/**
	 * Called whenever the form's dirty state changes. The parent uses this to
	 * drive the close-guard and route-guard.
	 */
	onDirtyChange?: (isDirty: boolean) => void;
	/** Imperative handle ref — pass a useRef<ScheduledJobFormHandle> to call submit() from outside. */
	ref?: Ref<ScheduledJobFormHandle>;
}

// Flatten a Zod issue path to a stable string key so per-field errors can be looked up by the input that owns
// them (mirrors the mcp form's issueKey helper).
function issueKey(path: readonly PropertyKey[]): string {
	return path.map((segment) => String(segment)).join(".");
}

// Bracket-notation lookup for the flattened error map (the strict tsconfig forbids dotted access on an index
// signature). Returns undefined when the field has no error so it can flow straight into Mantine's `error`.
function fieldError(errors: Record<string, string>, key: string): string | undefined {
	return errors[key];
}

// Shallow dirty check: compare each top-level key of current values to initialValues.
function computeIsDirty(current: ScheduledJobFormValues, initial: ScheduledJobFormValues): boolean {
	return (Object.keys(current) as Array<keyof ScheduledJobFormValues>).some((key) => current[key] !== initial[key]);
}

// Create/edit form for a scheduled job. The template picker constrains which schedule kinds are offered and
// supplies the defaults the form pre-fills on a fresh template selection. Fields are shown conditionally per the
// chosen schedule kind. The enabled flag is NOT edited here — a job is created disabled and enabling is a
// separate, deliberate row action. parameters is a write-only plaintext-JSON textarea; the backend re-validates.
export function ScheduledJobForm({
	initialValues,
	templates,
	isEditing,
	// isSubmitting and onCancel are kept in the interface for callers that render
	// the form standalone (outside a DialogShell footer), but the dialog variant
	// drives these from the footer — mark as unused here with underscore prefix.
	isSubmitting: _isSubmitting,
	submitError,
	onSubmit,
	onCancel: _onCancel,
	onDirtyChange,
	ref,
}: ScheduledJobFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<ScheduledJobFormValues>(initialValues);
	const [errors, setErrors] = useState<Record<string, string>>({});

	// Pure state update — no parent notification here. Calling onDirtyChange inside a setState updater runs during the
	// render phase and triggers React's "cannot update a component while rendering a different component" error.
	const updateValues = useCallback((updater: (current: ScheduledJobFormValues) => ScheduledJobFormValues) => {
		setValues(updater);
	}, []);

	// Report dirty state to the parent (which wires it to the close-guard / route-guard) from an effect, so the parent
	// setter is only ever called after commit — never during render. Fires on mount (a fresh mount is clean) and after
	// every edit.
	useEffect(() => {
		onDirtyChange?.(computeIsDirty(values, initialValues));
	}, [values, initialValues, onDirtyChange]);

	const templateData = useMemo(
		() => templates.map((template) => ({ value: template.templateId, label: template.displayName })),
		[templates],
	);

	const selectedTemplate = useMemo(
		() => templates.find((template) => template.templateId === values.templateId),
		[templates, values.templateId],
	);

	// The schedule kinds offered are constrained to those the selected template supports. Before a template is
	// chosen (create mode, empty templateId) there is no constraint yet, so the select offers every kind the
	// schema knows about; once a template is selected the list narrows to that template's supported kinds.
	const scheduleKindData = useMemo(() => {
		const supported = selectedTemplate?.supportedScheduleKinds ?? scheduleKinds;
		return supported.map((kind) => ({
			value: kind,
			label: t(`pages.scheduler.form.scheduleKind.options.${kind}`, kind),
		}));
	}, [selectedTemplate, t]);

	const misfireData = useMemo(
		() =>
			schedulerMisfirePolicies.map((policy) => ({
				value: policy,
				label: t(`pages.scheduler.form.misfirePolicy.options.${policy}`, policy),
			})),
		[t],
	);

	// Selecting a template (only meaningful on create) pre-fills its defaults — the default schedule kind, misfire
	// policy, max runtime, and any default parameter JSON — so the operator starts from the template's recommended
	// shape. On edit the template is fixed (changing it would re-key the job), so the picker is disabled.
	const handleTemplateChange = useCallback(
		(value: string | null) => {
			if (value === null) {
				return;
			}
			const template = templates.find((candidate) => candidate.templateId === value);
			updateValues((current) => ({
				...current,
				templateId: value,
				scheduleKind: template?.defaultScheduleKind ?? current.scheduleKind,
				misfirePolicy: template?.defaultMisfirePolicy ?? current.misfirePolicy,
				maxRuntimeSeconds:
					template?.defaultMaxRuntimeSeconds != null ? String(template.defaultMaxRuntimeSeconds) : current.maxRuntimeSeconds,
				parameters: template?.defaultParameters ?? current.parameters,
			}));
		},
		[templates, updateValues],
	);

	const handleScheduleKindChange = useCallback(
		(value: string | null) => {
			if (value === null) {
				return;
			}
			updateValues((current) => ({ ...current, scheduleKind: value as ScheduleKind }));
		},
		[updateValues],
	);

	const handleMisfireChange = useCallback(
		(value: string | null) => {
			if (value === null) {
				return;
			}
			updateValues((current) => ({ ...current, misfirePolicy: value as SchedulerMisfirePolicy }));
		},
		[updateValues],
	);

	const handleSubmit = useCallback(() => {
		const result = scheduledJobFormSchema.safeParse(values);
		if (!result.success) {
			const nextErrors: Record<string, string> = {};
			for (const issue of result.error.issues) {
				nextErrors[issueKey(issue.path)] = issue.message;
			}
			setErrors(nextErrors);
			return;
		}

		setErrors({});
		onSubmit(values);
	}, [onSubmit, values]);

	// Expose submit() so the DialogShell footer's Save button can trigger validation
	// without coupling the footer to internal form state.
	useImperativeHandle(ref, () => ({ submit: handleSubmit }), [handleSubmit]);

	// Stable per-key error lookup forwarded to the schedule-fields subcomponent.
	const lookupFieldError = useCallback((key: string) => fieldError(errors, key), [errors]);

	return (
		<Stack gap="md" data-testid="scheduled-job-form">
			<Select
				label={t("pages.scheduler.form.template.label", "Template")}
				description={t("pages.scheduler.form.template.description", "The job template determines what this job does.")}
				placeholder={t("pages.scheduler.form.template.placeholder", "Select a template")}
				data={templateData}
				value={values.templateId || null}
				disabled={isEditing}
				allowDeselect={false}
				error={fieldError(errors, "templateId")}
				onChange={handleTemplateChange}
				data-testid="scheduler-form-template"
			/>
			{selectedTemplate?.description ? (
				<Alert variant="light" data-testid="scheduler-form-template-description">
					{selectedTemplate.description}
				</Alert>
			) : null}

			<TextInput
				label={t("pages.scheduler.form.displayName.label", "Name")}
				placeholder={t("pages.scheduler.form.displayName.placeholder", "Nightly cleanup")}
				value={values.displayName}
				required={true}
				error={fieldError(errors, "displayName")}
				onChange={(event) => {
					const value = event.currentTarget.value;
					updateValues((current) => ({ ...current, displayName: value }));
				}}
				data-testid="scheduler-form-name"
			/>
			<Textarea
				label={t("pages.scheduler.form.description.label", "Description")}
				placeholder={t("pages.scheduler.form.description.placeholder", "Optional short summary")}
				value={values.description}
				autosize={true}
				minRows={2}
				onChange={(event) => {
					const value = event.currentTarget.value;
					updateValues((current) => ({ ...current, description: value }));
				}}
				data-testid="scheduler-form-description"
			/>

			<Select
				label={t("pages.scheduler.form.scheduleKind.label", "Schedule kind")}
				data={scheduleKindData}
				value={values.scheduleKind}
				allowDeselect={false}
				onChange={handleScheduleKindChange}
				data-testid="scheduler-form-schedule-kind"
			/>

			<ScheduledJobScheduleFields values={values} fieldError={lookupFieldError} onFieldChange={updateValues} />

			<TextInput
				label={t("pages.scheduler.form.timeZoneId.label", "Time zone")}
				placeholder="UTC"
				value={values.timeZoneId}
				required={true}
				error={fieldError(errors, "timeZoneId")}
				onChange={(event) => {
					const value = event.currentTarget.value;
					updateValues((current) => ({ ...current, timeZoneId: value }));
				}}
				data-testid="scheduler-form-timezone"
			/>

			<Select
				label={t("pages.scheduler.form.misfirePolicy.label", "Misfire policy")}
				description={t(
					"pages.scheduler.form.misfirePolicy.description",
					"How a missed fire is handled when the node was unavailable.",
				)}
				data={misfireData}
				value={values.misfirePolicy}
				allowDeselect={false}
				onChange={handleMisfireChange}
				data-testid="scheduler-form-misfire"
			/>

			<NumberInput
				label={t("pages.scheduler.form.maxRuntimeSeconds.label", "Max runtime (seconds)")}
				description={t("pages.scheduler.form.maxRuntimeSeconds.description", "Leave blank for no runtime limit.")}
				value={values.maxRuntimeSeconds === "" ? "" : Number(values.maxRuntimeSeconds)}
				min={1}
				allowDecimal={false}
				error={fieldError(errors, "maxRuntimeSeconds")}
				onChange={(value) => updateValues((current) => ({ ...current, maxRuntimeSeconds: value === "" ? "" : String(value) }))}
				data-testid="scheduler-form-max-runtime"
			/>

			<Switch
				label={t("pages.scheduler.form.preventOverlap.label", "Prevent overlapping runs")}
				checked={values.preventOverlap}
				onChange={(event) => {
					const checked = event.currentTarget.checked;
					updateValues((current) => ({ ...current, preventOverlap: checked }));
				}}
				data-testid="scheduler-form-prevent-overlap"
			/>

			<Textarea
				label={t("pages.scheduler.form.parameters.label", "Parameters (JSON)")}
				description={t(
					"pages.scheduler.form.parameters.description",
					"Optional template parameters as JSON. Stored encrypted and never shown again after saving.",
				)}
				value={values.parameters}
				autosize={true}
				minRows={3}
				onChange={(event) => {
					const value = event.currentTarget.value;
					updateValues((current) => ({ ...current, parameters: value }));
				}}
				data-testid="scheduler-form-parameters"
			/>

			{submitError ? (
				<Alert color="red" data-testid="scheduler-form-submit-error">
					{submitError}
				</Alert>
			) : null}
		</Stack>
	);
}
