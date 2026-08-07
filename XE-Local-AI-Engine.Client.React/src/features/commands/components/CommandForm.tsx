import { Alert, Stack, Textarea, TextInput } from "@mantine/core";
import { type Ref, useCallback, useEffect, useImperativeHandle, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { type CommandFormValues, commandFormSchema } from "@/features/commands/models/CommandModels";

export interface CommandFormHandle {
	submit: () => void;
}

interface CommandFormProps {
	initialValues: CommandFormValues;
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: CommandFormValues) => void;
	onDirtyChange?: (dirty: boolean) => void;
	ref?: Ref<CommandFormHandle>;
}

export function CommandForm({ initialValues, isSubmitting, submitError, onSubmit, onDirtyChange, ref }: CommandFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState(initialValues);
	const [errors, setErrors] = useState<Record<string, string>>({});
	const valuesRef = useRef(values);
	valuesRef.current = values;

	const update = useCallback((field: keyof CommandFormValues, value: string) => {
		const next = { ...valuesRef.current, [field]: value };
		valuesRef.current = next;
		setValues(next);
	}, []);

	useEffect(() => {
		onDirtyChange?.(JSON.stringify(values) !== JSON.stringify(initialValues));
	}, [initialValues, onDirtyChange, values]);

	const submit = useCallback(() => {
		const result = commandFormSchema.safeParse(values);
		if (!result.success) {
			setErrors(Object.fromEntries(result.error.issues.map((issue) => [String(issue.path[0]), issue.message])));
			return;
		}
		setErrors({});
		onSubmit(result.data);
	}, [onSubmit, values]);

	useImperativeHandle(ref, () => ({ submit }), [submit]);

	return (
		<Stack gap="md" data-testid="command-form">
			{submitError ? <Alert color="red">{submitError}</Alert> : null}
			<TextInput
				label={t("pages.commands.form.name.label")}
				description={t("pages.commands.form.name.description")}
				leftSection="/"
				placeholder={t("pages.commands.form.name.placeholder")}
				value={values.name}
				required={true}
				disabled={isSubmitting}
				error={errors["name"] ? t("pages.commands.form.name.invalid") : undefined}
				onChange={(event) => update("name", event.currentTarget.value.toLowerCase())}
				data-testid="command-form-name"
			/>
			<Textarea
				label={t("pages.commands.form.description.label")}
				placeholder={t("pages.commands.form.description.placeholder")}
				value={values.description}
				disabled={isSubmitting}
				error={errors["description"]}
				onChange={(event) => update("description", event.currentTarget.value)}
				data-testid="command-form-description"
			/>
			<TextInput
				label={t("pages.commands.form.action.label")}
				value={t("pages.commands.form.action.sendPrompt")}
				readOnly={true}
				data-testid="command-form-action"
			/>
			<Textarea
				label={t("pages.commands.form.prompt.label")}
				description={t("pages.commands.form.prompt.description")}
				placeholder={t("pages.commands.form.prompt.placeholder")}
				value={values.prompt}
				required={true}
				autosize={true}
				minRows={5}
				disabled={isSubmitting}
				error={errors["prompt"] ? t("pages.commands.form.prompt.required") : undefined}
				onChange={(event) => update("prompt", event.currentTarget.value)}
				data-testid="command-form-prompt"
			/>
		</Stack>
	);
}
