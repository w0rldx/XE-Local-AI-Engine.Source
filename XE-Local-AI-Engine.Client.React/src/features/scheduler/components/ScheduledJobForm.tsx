import { Alert, Button, Group, NumberInput, Select, Stack, Switch, Textarea, TextInput } from "@mantine/core";
import { IconDeviceFloppy, IconX } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	type ScheduledJobFormValues,
	scheduledJobFormSchema,
	type ScheduledJobTemplate,
	type ScheduleKind,
	scheduleKinds,
	type SchedulerMisfirePolicy,
	schedulerMisfirePolicies,
} from "@/features/scheduler/models/SchedulerModels";

interface ScheduledJobFormProps {
	initialValues: ScheduledJobFormValues;
	templates: readonly ScheduledJobTemplate[];
	isEditing: boolean;
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: ScheduledJobFormValues) => void;
	onCancel: () => void;
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

// Create/edit form for a scheduled job. The template picker constrains which schedule kinds are offered and
// supplies the defaults the form pre-fills on a fresh template selection. Fields are shown conditionally per the
// chosen schedule kind. The enabled flag is NOT edited here — a job is created disabled and enabling is a
// separate, deliberate row action. parameters is a write-only plaintext-JSON textarea; the backend re-validates.
export function ScheduledJobForm({
	initialValues,
	templates,
	isEditing,
	isSubmitting,
	submitError,
	onSubmit,
	onCancel,
}: ScheduledJobFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<ScheduledJobFormValues>(initialValues);
	const [errors, setErrors] = useState<Record<string, string>>({});

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
			setValues((current) => ({
				...current,
				templateId: value,
				scheduleKind: template?.defaultScheduleKind ?? current.scheduleKind,
				misfirePolicy: template?.defaultMisfirePolicy ?? current.misfirePolicy,
				maxRuntimeSeconds:
					template?.defaultMaxRuntimeSeconds != null ? String(template.defaultMaxRuntimeSeconds) : current.maxRuntimeSeconds,
				parameters: template?.defaultParameters ?? current.parameters,
			}));
		},
		[templates],
	);

	const handleScheduleKindChange = useCallback((value: string | null) => {
		if (value === null) {
			return;
		}
		setValues((current) => ({ ...current, scheduleKind: value as ScheduleKind }));
	}, []);

	const handleMisfireChange = useCallback((value: string | null) => {
		if (value === null) {
			return;
		}
		setValues((current) => ({ ...current, misfirePolicy: value as SchedulerMisfirePolicy }));
	}, []);

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

	const isCron = values.scheduleKind === "Cron";
	const isInterval = values.scheduleKind === "SimpleInterval";
	const isOneShot = values.scheduleKind === "OneShot";
	// A Manual job is a durable on-demand job with no trigger — it has no cron/interval/start-at fields, so those
	// inputs are hidden and a short note explains that it runs only when triggered.
	const isManual = values.scheduleKind === "Manual";

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
					setValues((current) => ({ ...current, displayName: value }));
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
					setValues((current) => ({ ...current, description: value }));
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

			{isManual ? (
				<Alert variant="light" data-testid="scheduler-form-manual-note">
					{t("pages.scheduler.form.scheduleKind.manualNote", "Runs only when triggered manually.")}
				</Alert>
			) : null}

			{isCron ? (
				<TextInput
					label={t("pages.scheduler.form.cronExpression.label", "Cron expression")}
					description={t("pages.scheduler.form.cronExpression.description", "Quartz cron syntax, e.g. 0 0 3 * * ?")}
					placeholder="0 0 3 * * ?"
					value={values.cronExpression}
					required={true}
					error={fieldError(errors, "cronExpression")}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, cronExpression: value }));
					}}
					data-testid="scheduler-form-cron"
				/>
			) : null}

			{isInterval ? (
				<Group grow={true} align="flex-start">
					<TextInput
						label={t("pages.scheduler.form.intervalSeconds.label", "Interval (seconds)")}
						placeholder="300"
						value={values.intervalSeconds}
						required={true}
						error={fieldError(errors, "intervalSeconds")}
						onChange={(event) => {
							const value = event.currentTarget.value;
							setValues((current) => ({ ...current, intervalSeconds: value }));
						}}
						data-testid="scheduler-form-interval"
					/>
					<TextInput
						label={t("pages.scheduler.form.repeatCount.label", "Repeat count")}
						description={t("pages.scheduler.form.repeatCount.description", "Leave blank to repeat forever.")}
						value={values.repeatCount}
						error={fieldError(errors, "repeatCount")}
						onChange={(event) => {
							const value = event.currentTarget.value;
							setValues((current) => ({ ...current, repeatCount: value }));
						}}
						data-testid="scheduler-form-repeat-count"
					/>
				</Group>
			) : null}

			{isOneShot ? (
				<TextInput
					type="datetime-local"
					label={t("pages.scheduler.form.startAtUtc.label", "Start at")}
					value={values.startAtUtc}
					required={true}
					error={fieldError(errors, "startAtUtc")}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setValues((current) => ({ ...current, startAtUtc: value }));
					}}
					data-testid="scheduler-form-start-at"
				/>
			) : null}

			<TextInput
				label={t("pages.scheduler.form.timeZoneId.label", "Time zone")}
				placeholder="UTC"
				value={values.timeZoneId}
				required={true}
				error={fieldError(errors, "timeZoneId")}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, timeZoneId: value }));
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
				onChange={(value) => setValues((current) => ({ ...current, maxRuntimeSeconds: value === "" ? "" : String(value) }))}
				data-testid="scheduler-form-max-runtime"
			/>

			<Switch
				label={t("pages.scheduler.form.preventOverlap.label", "Prevent overlapping runs")}
				checked={values.preventOverlap}
				onChange={(event) => {
					const checked = event.currentTarget.checked;
					setValues((current) => ({ ...current, preventOverlap: checked }));
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
					setValues((current) => ({ ...current, parameters: value }));
				}}
				data-testid="scheduler-form-parameters"
			/>

			{submitError ? (
				<Alert color="red" data-testid="scheduler-form-submit-error">
					{submitError}
				</Alert>
			) : null}
			<Group justify="flex-end">
				<Button
					variant="subtle"
					leftSection={<IconX size={16} />}
					onClick={onCancel}
					disabled={isSubmitting}
					data-testid="scheduler-form-cancel"
				>
					{t("common.cancel", "Cancel")}
				</Button>
				<Button
					leftSection={<IconDeviceFloppy size={16} />}
					onClick={handleSubmit}
					loading={isSubmitting}
					data-testid="scheduler-form-submit"
				>
					{t("common.save", "Save")}
				</Button>
			</Group>
		</Stack>
	);
}
