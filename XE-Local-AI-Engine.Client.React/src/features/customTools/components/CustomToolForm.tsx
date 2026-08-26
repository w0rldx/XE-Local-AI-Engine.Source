import { Alert, Checkbox, Divider, SegmentedControl, Stack, Switch, Text, Textarea, TextInput } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { type Ref, useCallback, useEffect, useImperativeHandle, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { CommandEditor, HttpEditor, ParameterBuilder } from "@/features/customTools/components/CustomToolEditors";
import { errorAt, type FieldErrors } from "@/features/customTools/models/CustomToolFormErrors";
import {
	CUSTOM_TOOL_NAME_PREFIX,
	type CustomToolFormValues,
	customToolFormSchema,
} from "@/features/customTools/models/CustomToolModels";

// Imperative handle so the host dialog can place Save in its sticky footer yet still trigger validate-then-submit.
export interface CustomToolFormHandle {
	submit: () => void;
}

interface CustomToolFormProps {
	initialValues: CustomToolFormValues;
	isSubmitting: boolean;
	submitError?: string;
	// Shown only on edit (mirrors the Skills form): a new tool is created disabled-off is not offered here — enabling is
	// a deliberate act the operator takes in the editor once satisfied.
	showEnabledToggle: boolean;
	onSubmit: (values: CustomToolFormValues) => void;
	onCancel: () => void;
	ref?: Ref<CustomToolFormHandle>;
	onDirtyChange?: (isDirty: boolean) => void;
	/** Reports the danger acknowledgement so the host footer can gate Save on it (the server also enforces it). */
	onAcknowledgedChange?: (acknowledged: boolean) => void;
}

// Create/edit form for a node custom tool. Controlled Mantine inputs validated with the shared Zod schema on submit.
// A prominent danger note and a mandatory acknowledgement gate the Save button — the same acknowledgement the backend
// enforces on every write. Both kind editors keep their own draft so switching kind never loses data.
export function CustomToolForm({
	initialValues,
	isSubmitting: _isSubmitting,
	submitError,
	showEnabledToggle,
	onSubmit,
	onCancel: _onCancel,
	ref,
	onDirtyChange,
	onAcknowledgedChange,
}: CustomToolFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<CustomToolFormValues>(initialValues);
	const [errors, setErrors] = useState<FieldErrors>({});

	// Pure state update — parent notification happens from an effect below, never during render.
	const update = useCallback((updater: (current: CustomToolFormValues) => CustomToolFormValues) => {
		setValues(updater);
	}, []);

	useEffect(() => {
		onDirtyChange?.(JSON.stringify(values) !== JSON.stringify(initialValues));
	}, [values, initialValues, onDirtyChange]);

	useEffect(() => {
		onAcknowledgedChange?.(values.acknowledged);
	}, [values.acknowledged, onAcknowledgedChange]);

	const handleSubmit = useCallback(() => {
		const result = customToolFormSchema.safeParse(values);
		if (!result.success) {
			const next: FieldErrors = {};
			for (const issue of result.error.issues) {
				const key = issue.path.join(".");
				if (!next[key]) {
					next[key] = issue.message;
				}
			}
			setErrors(next);
			return;
		}
		setErrors({});
		onSubmit(result.data as CustomToolFormValues);
	}, [onSubmit, values]);

	useImperativeHandle(ref, () => ({ submit: handleSubmit }), [handleSubmit]);

	const nameError = useMemo(() => {
		if (!errorAt(errors, "name")) {
			return undefined;
		}
		return values.name.trim().length === 0
			? t("pages.customTools.form.name.required", "Name is required.")
			: t("pages.customTools.form.name.invalid", "Use lowercase letters, digits and underscores; start and end alphanumeric.");
	}, [errors, values.name, t]);

	return (
		<Stack gap="md" data-testid="custom-tool-form">
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="custom-tool-form-danger-note">
				{t(
					"pages.customTools.form.dangerNote",
					"Custom tools run on this machine: an HTTP tool reaches the network and a Command tool launches a program with your access. Only create and enable tools whose exact behaviour you trust.",
				)}
			</Alert>

			<TextInput
				label={t("pages.customTools.form.name.label", "Name")}
				description={t("pages.customTools.form.name.description", "The model sees this as {{prefixed}}.", {
					prefixed: `${CUSTOM_TOOL_NAME_PREFIX}${values.name || "name"}`,
				})}
				placeholder="weather"
				leftSection={
					<Text size="xs" c="dimmed" pl={4}>
						{CUSTOM_TOOL_NAME_PREFIX}
					</Text>
				}
				leftSectionWidth={72}
				value={values.name}
				required={true}
				error={nameError}
				onChange={(event) => {
					const value = event.currentTarget.value;
					update((current) => ({ ...current, name: value }));
				}}
				data-testid="custom-tool-form-name"
			/>
			<Textarea
				label={t("pages.customTools.form.description.label", "Description")}
				description={t(
					"pages.customTools.form.description.description",
					"What the tool does — the model reads this to decide when to call it.",
				)}
				value={values.description}
				required={true}
				autosize={true}
				minRows={2}
				error={
					errorAt(errors, "description")
						? t("pages.customTools.form.description.required", "Description is required.")
						: undefined
				}
				onChange={(event) => {
					const value = event.currentTarget.value;
					update((current) => ({ ...current, description: value }));
				}}
				data-testid="custom-tool-form-description"
			/>

			<Stack gap={4}>
				<Text size="sm" fw={500}>
					{t("pages.customTools.form.kind.label", "Kind")}
				</Text>
				<SegmentedControl
					value={values.kind}
					onChange={(value) => update((current) => ({ ...current, kind: value as CustomToolFormValues["kind"] }))}
					data={[
						{ label: t("pages.customTools.form.kind.http", "HTTP fetch"), value: "HttpFetch" },
						{ label: t("pages.customTools.form.kind.command", "Command"), value: "Command" },
					]}
					data-testid="custom-tool-form-kind"
				/>
			</Stack>

			<Stack gap={4}>
				<Text size="sm" fw={500}>
					{t("pages.customTools.form.mode.label", "Mode")}
				</Text>
				<SegmentedControl
					value={values.mode}
					onChange={(value) => update((current) => ({ ...current, mode: value as CustomToolFormValues["mode"] }))}
					data={[
						{ label: t("pages.customTools.form.mode.fixed", "Fixed"), value: "Fixed" },
						{ label: t("pages.customTools.form.mode.parameterized", "Parameterized"), value: "Parameterized" },
					]}
					data-testid="custom-tool-form-mode"
				/>
				<Text size="xs" c="dimmed">
					{values.mode === "Parameterized"
						? t(
								"pages.customTools.form.mode.parameterizedHint",
								"The model fills the declared inputs into the {param} placeholders each call.",
							)
						: t("pages.customTools.form.mode.fixedHint", "The tool runs verbatim with no model-supplied input.")}
				</Text>
				{errorAt(errors, "mode") ? (
					<Text size="xs" c="red">
						{t(
							"pages.customTools.form.mode.fixedNoParameters",
							"A Fixed tool cannot declare parameters. Remove them or switch to Parameterized.",
						)}
					</Text>
				) : null}
			</Stack>

			{values.mode === "Parameterized" ? <ParameterBuilder values={values} errors={errors} update={update} /> : null}

			<Divider />

			{values.kind === "HttpFetch" ? (
				<HttpEditor values={values} errors={errors} update={update} />
			) : (
				<CommandEditor values={values} errors={errors} update={update} />
			)}

			<Divider />

			<Checkbox
				label={t(
					"pages.customTools.form.acknowledge.label",
					"I understand these tools can run code, call networks, and launch programs on my machine, and I take responsibility.",
				)}
				checked={values.acknowledged}
				onChange={(event) => {
					const checked = event.currentTarget.checked;
					update((current) => ({ ...current, acknowledged: checked }));
				}}
				error={
					errorAt(errors, "acknowledged")
						? t("pages.customTools.form.acknowledge.required", "You must acknowledge this to save.")
						: undefined
				}
				data-testid="custom-tool-form-acknowledge"
			/>

			{showEnabledToggle ? (
				<Switch
					label={t("pages.customTools.form.enabled.label", "Enabled")}
					description={t(
						"pages.customTools.form.enabled.description",
						"A disabled tool is never offered to any agent, even if assigned.",
					)}
					checked={values.enabled}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						update((current) => ({ ...current, enabled: checked }));
					}}
					data-testid="custom-tool-form-enabled"
				/>
			) : null}

			{submitError ? (
				<Alert color="red" data-testid="custom-tool-form-submit-error">
					{submitError}
				</Alert>
			) : null}
		</Stack>
	);
}
