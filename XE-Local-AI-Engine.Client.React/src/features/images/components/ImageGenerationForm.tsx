import { Alert, Button, Group, NumberInput, Select, Stack, Textarea } from "@mantine/core";
import { IconSparkles } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	type ImageGenerationFormValues,
	imageFormDefaults,
	imageGenerationFormSchema,
	type ImageModelView,
	imageSamplers,
} from "@/features/images/models/ImageModels";

// Flatten a Zod issue path to a stable string key so per-field errors can be looked up by the input that owns them
// (mirrors ScheduledJobForm.issueKey).
function issueKey(path: readonly PropertyKey[]): string {
	return path.map((segment) => String(segment)).join(".");
}

function fieldError(errors: Record<string, string>, key: string): string | undefined {
	return errors[key];
}

interface ImageGenerationFormProps {
	models: readonly ImageModelView[];
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: ImageGenerationFormValues) => void;
}

// Text-to-image generation form. Schema-first: on submit the values are validated with the shared Zod schema and any
// issues are mapped to their owning field (client-side); a server-side failure is surfaced as a submit-level alert
// (this codebase's ProblemDetails carries no per-field error map — same posture as McpServerForm). The model picker is
// sourced from the installed image models; with none installed the form disables so a job can't be enqueued modelless.
export function ImageGenerationForm({ models, isSubmitting, submitError, onSubmit }: ImageGenerationFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<ImageGenerationFormValues>(() => ({
		...imageFormDefaults,
		modelName: models[0]?.modelName ?? "",
	}));
	const [errors, setErrors] = useState<Record<string, string>>({});

	const modelData = useMemo(() => models.map((model) => ({ value: model.modelName, label: model.modelName })), [models]);
	const samplerData = useMemo(
		() => imageSamplers.map((sampler) => ({ value: sampler, label: t(`pages.images.form.samplers.${sampler}`, sampler) })),
		[t],
	);

	const hasModels = models.length > 0;

	const handleSubmit = useCallback(() => {
		// Ensure a model is selected even if the picker was never touched (first model auto-selected below via value).
		const candidate: ImageGenerationFormValues = {
			...values,
			modelName: values.modelName || models[0]?.modelName || "",
			negativePrompt: values.negativePrompt?.trim() ? values.negativePrompt : undefined,
		};
		const result = imageGenerationFormSchema.safeParse(candidate);
		if (!result.success) {
			const nextErrors: Record<string, string> = {};
			for (const issue of result.error.issues) {
				nextErrors[issueKey(issue.path)] = issue.message;
			}
			setErrors(nextErrors);
			return;
		}
		setErrors({});
		onSubmit(result.data);
	}, [models, onSubmit, values]);

	const selectedModel = values.modelName || models[0]?.modelName || null;

	return (
		<Stack gap="md" data-testid="image-generation-form">
			<Select
				label={t("pages.images.form.model.label", "Model")}
				placeholder={t("pages.images.form.model.placeholder", "Select an image model")}
				data={modelData}
				value={selectedModel}
				disabled={!hasModels}
				allowDeselect={false}
				error={fieldError(errors, "modelName")}
				onChange={(value) => setValues((current) => ({ ...current, modelName: value ?? "" }))}
				data-testid="image-form-model"
			/>

			<Textarea
				label={t("pages.images.form.prompt.label", "Prompt")}
				placeholder={t("pages.images.form.prompt.placeholder", "A watercolor fox in a misty forest")}
				value={values.prompt}
				required={true}
				autosize={true}
				minRows={2}
				error={fieldError(errors, "prompt")}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, prompt: value }));
				}}
				data-testid="image-form-prompt"
			/>

			<Textarea
				label={t("pages.images.form.negativePrompt.label", "Negative prompt")}
				placeholder={t("pages.images.form.negativePrompt.placeholder", "blurry, low quality")}
				value={values.negativePrompt ?? ""}
				autosize={true}
				minRows={1}
				error={fieldError(errors, "negativePrompt")}
				onChange={(event) => {
					const value = event.currentTarget.value;
					setValues((current) => ({ ...current, negativePrompt: value }));
				}}
				data-testid="image-form-negative-prompt"
			/>

			<Group grow={true} align="flex-start">
				<NumberInput
					label={t("pages.images.form.width.label", "Width")}
					value={values.width}
					min={64}
					max={2048}
					step={64}
					allowDecimal={false}
					error={fieldError(errors, "width")}
					onChange={(value) => setValues((current) => ({ ...current, width: typeof value === "number" ? value : current.width }))}
					data-testid="image-form-width"
				/>
				<NumberInput
					label={t("pages.images.form.height.label", "Height")}
					value={values.height}
					min={64}
					max={2048}
					step={64}
					allowDecimal={false}
					error={fieldError(errors, "height")}
					onChange={(value) => setValues((current) => ({ ...current, height: typeof value === "number" ? value : current.height }))}
					data-testid="image-form-height"
				/>
			</Group>

			<Group grow={true} align="flex-start">
				<NumberInput
					label={t("pages.images.form.steps.label", "Steps")}
					value={values.steps}
					min={1}
					max={150}
					allowDecimal={false}
					error={fieldError(errors, "steps")}
					onChange={(value) => setValues((current) => ({ ...current, steps: typeof value === "number" ? value : current.steps }))}
					data-testid="image-form-steps"
				/>
				<NumberInput
					label={t("pages.images.form.cfgScale.label", "CFG scale")}
					value={values.cfgScale}
					min={1}
					max={30}
					step={0.5}
					decimalScale={1}
					error={fieldError(errors, "cfgScale")}
					onChange={(value) => setValues((current) => ({ ...current, cfgScale: typeof value === "number" ? value : current.cfgScale }))}
					data-testid="image-form-cfg-scale"
				/>
			</Group>

			<Group grow={true} align="flex-start">
				<Select
					label={t("pages.images.form.sampler.label", "Sampler")}
					data={samplerData}
					value={values.sampler}
					allowDeselect={false}
					error={fieldError(errors, "sampler")}
					onChange={(value) => setValues((current) => ({ ...current, sampler: (value ?? current.sampler) as ImageGenerationFormValues["sampler"] }))}
					data-testid="image-form-sampler"
				/>
				<NumberInput
					label={t("pages.images.form.seed.label", "Seed")}
					description={t("pages.images.form.seed.description", "-1 for a random seed")}
					value={values.seed}
					min={-1}
					allowDecimal={false}
					error={fieldError(errors, "seed")}
					onChange={(value) => setValues((current) => ({ ...current, seed: typeof value === "number" ? value : current.seed }))}
					data-testid="image-form-seed"
				/>
			</Group>

			{!hasModels ? (
				<Alert color="yellow" data-testid="image-form-no-models">
					{t("pages.images.form.noModels", "Install an image model below before generating.")}
				</Alert>
			) : null}

			{submitError ? (
				<Alert color="red" data-testid="image-form-submit-error">
					{submitError}
				</Alert>
			) : null}

			<Group justify="flex-end">
				<Button
					leftSection={<IconSparkles size={16} />}
					loading={isSubmitting}
					disabled={!hasModels}
					onClick={handleSubmit}
					data-testid="image-form-submit"
				>
					{t("pages.images.form.submit", "Generate")}
				</Button>
			</Group>
		</Stack>
	);
}
