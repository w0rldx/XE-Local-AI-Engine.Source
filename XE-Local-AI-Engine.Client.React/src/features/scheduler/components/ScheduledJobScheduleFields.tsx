import { Alert, Group, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { ScheduledJobFormValues } from "@/features/scheduler/models/SchedulerModels";

interface ScheduledJobScheduleFieldsProps {
	values: ScheduledJobFormValues;
	// Bracket-notation lookup over the flattened Zod error map (mirrors the parent form's fieldError helper).
	fieldError: (key: string) => string | undefined;
	// Single-field updater shared with the parent form so dirty-state reporting stays in one place.
	onFieldChange: (updater: (current: ScheduledJobFormValues) => ScheduledJobFormValues) => void;
}

// The schedule-kind-conditional inputs of the scheduled-job form: a Manual note, the Cron expression, the
// SimpleInterval pair (interval + repeat count), and the OneShot start-at. Split out of ScheduledJobForm to keep
// the parent readable; it owns only presentation and forwards edits through onFieldChange.
export function ScheduledJobScheduleFields({ values, fieldError, onFieldChange }: ScheduledJobScheduleFieldsProps) {
	const { t } = useTranslation();

	const isCron = values.scheduleKind === "Cron";
	const isInterval = values.scheduleKind === "SimpleInterval";
	const isOneShot = values.scheduleKind === "OneShot";
	// A Manual job is a durable on-demand job with no trigger — it has no cron/interval/start-at fields, so those
	// inputs are hidden and a short note explains that it runs only when triggered.
	const isManual = values.scheduleKind === "Manual";

	return (
		<>
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
					error={fieldError("cronExpression")}
					onChange={(event) => {
						const value = event.currentTarget.value;
						onFieldChange((current) => ({ ...current, cronExpression: value }));
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
						error={fieldError("intervalSeconds")}
						onChange={(event) => {
							const value = event.currentTarget.value;
							onFieldChange((current) => ({ ...current, intervalSeconds: value }));
						}}
						data-testid="scheduler-form-interval"
					/>
					<TextInput
						label={t("pages.scheduler.form.repeatCount.label", "Repeat count")}
						description={t("pages.scheduler.form.repeatCount.description", "Leave blank to repeat forever.")}
						value={values.repeatCount}
						error={fieldError("repeatCount")}
						onChange={(event) => {
							const value = event.currentTarget.value;
							onFieldChange((current) => ({ ...current, repeatCount: value }));
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
					error={fieldError("startAtUtc")}
					onChange={(event) => {
						const value = event.currentTarget.value;
						onFieldChange((current) => ({ ...current, startAtUtc: value }));
					}}
					data-testid="scheduler-form-start-at"
				/>
			) : null}
		</>
	);
}
