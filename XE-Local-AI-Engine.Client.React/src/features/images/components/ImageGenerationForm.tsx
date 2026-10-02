import {
	Alert,
	Button,
	Group,
	NumberInput,
	SegmentedControl,
	Select,
	SimpleGrid,
	Slider,
	Stack,
	Text,
	Textarea,
} from "@mantine/core";
import { IconSparkles } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { fieldError, issueKey } from "@/core/ui/forms/ZodFieldErrors";
import { AssistActions } from "@/features/assist/components/AssistActions";
import type { AssistDraft } from "@/features/assist/models/AssistModels";
import { ImageSourceThumbnail } from "@/features/images/components/ImageSourceThumbnail";
import {
	clearImageFormOverrides,
	imageFormValuesForModel,
	readImageFormOverrides,
	writeImageFormOverrides,
} from "@/features/images/models/ImageFormOverrides";
import {
	defaultImageEditStrength,
	editModeLabel,
	fitEditDimensions,
	type ImageEditMode,
	type ImageEditSource,
	type ImageGenerationFormValues,
	type ImageModelView,
	imageFormDefaultsForModel,
	imageGenerationFormSchema,
	imageSamplers,
} from "@/features/images/models/ImageModels";

// Image prompts carry no provenance to drop on discard.
const noop = () => undefined;

interface ImageGenerationFormProps {
	models: readonly ImageModelView[];
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: ImageGenerationFormValues) => void;
	/** The image being edited; null (the default) is plain text-to-image. */
	editSource?: ImageEditSource | null;
	/** Leaves edit mode; the page clears `editSource`. */
	onCancelEdit?: () => void;
}

type EditFields = Pick<ImageGenerationFormValues, "editMode" | "strength"> &
	Partial<Pick<ImageGenerationFormValues, "width" | "height">>;

/**
 * The edit fields a model and source imply. The mode the operator picked survives a model switch only when the new
 * model offers it; the size is re-fitted to the new model's native pixel count, because a size right for SD1.5 is a
 * quarter of what SDXL was trained at. Leaving edit mode clears mode and strength and keeps the size.
 */
function editFieldsFor(
	model: ImageModelView | undefined,
	source: ImageEditSource | null,
	current: Pick<ImageGenerationFormValues, "editMode" | "strength">,
): EditFields {
	if (source === null) {
		return { editMode: undefined, strength: undefined };
	}
	const modes = model?.editModes ?? [];
	const editMode = current.editMode !== undefined && modes.includes(current.editMode) ? current.editMode : modes[0];
	return {
		editMode,
		strength: current.strength ?? defaultImageEditStrength,
		...(model === undefined ? {} : fitEditDimensions(source, model.nativePixels)),
	};
}

// Text-to-image generation form. Schema-first: on submit the values are validated with the shared Zod schema and any
// issues are mapped to their owning field (client-side); a server-side failure is surfaced as a submit-level alert
// (this codebase's ProblemDetails carries no per-field error map — same posture as McpServerForm). The model picker is
// sourced from the installed image models; with none installed the form disables so a job can't be enqueued modelless.
export function ImageGenerationForm({
	models,
	isSubmitting,
	submitError,
	onSubmit,
	editSource = null,
	onCancelEdit,
}: ImageGenerationFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<ImageGenerationFormValues>(() => imageFormValuesForModel(models[0]));
	const [errors, setErrors] = useState<Record<string, string>>({});

	// Entering edit mode (or picking a different source) re-derives mode, strength and size; leaving it clears them.
	// Adjusted during render against the last applied source rather than in an effect, so the first paint of the edit
	// form already shows the fitted size and a refetched model list never overwrites a size the operator typed.
	const [appliedSource, setAppliedSource] = useState<ImageEditSource | null>(null);
	if (editSource !== appliedSource) {
		setAppliedSource(editSource);
		setValues((current) => ({
			...current,
			...editFieldsFor(
				models.find((model) => model.modelName === current.modelName),
				editSource,
				// A new source starts from the defaults, not from the previous edit's mode and strength.
				{ editMode: undefined, strength: undefined },
			),
		}));
	}

	// Picking a different model re-seeds the sampling parameters from ITS family, keeping the prompts. The families
	// disagree sharply — FLUX-schnell wants ~4 steps at CFG 1.0 where SD1.5 wants 20 at 7.0 — and carrying one family's
	// numbers into another produces a bad image rather than an error, which is far harder to diagnose than a failure.
	// The operator's remembered override for that model (ImageFormOverrides) wins over the family numbers.
	const handleModelChange = useCallback(
		(modelName: string) => {
			const model = models.find((candidate) => candidate.modelName === modelName);
			setValues((current) => ({
				...imageFormValuesForModel(model),
				modelName,
				prompt: current.prompt,
				negativePrompt: current.negativePrompt,
				width: current.width,
				height: current.height,
				seed: current.seed,
				...editFieldsFor(model, editSource, current),
			}));
		},
		[editSource, models],
	);

	const modelData = useMemo(() => models.map((model) => ({ value: model.modelName, label: model.modelName })), [models]);
	const samplerData = useMemo(
		() => imageSamplers.map((sampler) => ({ value: sampler, label: t(`pages.images.form.samplers.${sampler}`, sampler) })),
		[t],
	);

	const hasModels = models.length > 0;

	// Steps, CFG and sampler are remembered per model as the operator edits them; the other fields are not.
	const updateSampling = (patch: Partial<Pick<ImageGenerationFormValues, "steps" | "cfgScale" | "sampler">>) => {
		const next = { ...values, ...patch };
		setValues(next);
		writeImageFormOverrides(next.modelName, next);
	};
	const hasOverride = readImageFormOverrides(values.modelName) !== null;
	const resetSampling = () => {
		clearImageFormOverrides(values.modelName);
		const { steps, cfgScale, sampler } = imageFormDefaultsForModel(models.find((model) => model.modelName === values.modelName));
		setValues((current) => ({ ...current, steps, cfgScale, sampler }));
	};

	const handleSubmit = useCallback(() => {
		// Ensure a model is selected even if the picker was never touched (first model auto-selected below via value).
		const candidate: ImageGenerationFormValues = {
			...values,
			modelName: values.modelName || models[0]?.modelName || "",
			negativePrompt: values.negativePrompt?.trim() ? values.negativePrompt : undefined,
			// The edit fields ride only while editing, and strength only with img2img — the node refuses it otherwise.
			editMode: editSource === null ? undefined : values.editMode,
			sourceImageId: editSource?.imageId,
			strength: editSource !== null && values.editMode === "img2img" ? values.strength : undefined,
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
	}, [editSource, models, onSubmit, values]);

	// Reconcile the selection against the models that actually exist.
	//
	// Two cases, and the second is easy to miss. The list arrives after the first render, so an empty selection has to
	// adopt the first model (and its family defaults, or the pick would silently run on the generic fallback numbers).
	// But a selection can also go stale *while* it is non-empty — deleting the selected model leaves the Select pointing
	// at a value no longer in its options, and Generate happily submits the deleted name and creates a job that fails.
	// Keying on "is the current name still present" covers both; the prompt fields are untouched either way.
	useEffect(() => {
		const first = models[0];
		if (first === undefined) {
			return;
		}
		const stillInstalled = models.some((model) => model.modelName === values.modelName);
		if (!stillInstalled) {
			handleModelChange(first.modelName);
		}
	}, [handleModelChange, models, values.modelName]);

	// A drafted prompt replaces the prompt; a drafted negative prompt replaces the current one only when the model gave one.
	const applyPromptDraft = useCallback((draft: AssistDraft) => {
		setValues((current) => ({
			...current,
			prompt: draft.content,
			negativePrompt: draft.description.trim() ? draft.description : current.negativePrompt,
		}));
	}, []);
	const assistExisting = useMemo(
		() => ({ name: "", description: values.negativePrompt ?? "", content: values.prompt }),
		[values.negativePrompt, values.prompt],
	);

	const selectedModel = models.some((model) => model.modelName === values.modelName)
		? values.modelName
		: (models[0]?.modelName ?? null);

	const editModes = models.find((model) => model.modelName === selectedModel)?.editModes ?? [];
	const editModeData = editModes.map((mode) => ({ value: mode, label: editModeLabel(t, mode) }));
	const cannotEdit = editSource !== null && editModes.length === 0;
	const promptPlaceholder =
		editSource !== null && values.editMode === "reference"
			? t("pages.images.edit.referencePlaceholder", "Describe the change")
			: t("pages.images.form.prompt.placeholder", "A watercolor fox in a misty forest");

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
				onChange={(value) => handleModelChange(value ?? "")}
				data-testid="image-form-model"
			/>

			{editSource === null ? null : (
				<Stack gap="xs" data-testid="image-form-edit">
					<Group justify="space-between" align="center" wrap="nowrap">
						<ImageSourceThumbnail
							imageId={editSource.imageId}
							label={t("pages.images.edit.editingFrom", "Editing from")}
							data-testid="image-form-edit-source"
						/>
						<Button variant="subtle" size="xs" onClick={onCancelEdit} data-testid="image-form-cancel-edit">
							{t("pages.images.edit.cancel", "Cancel edit")}
						</Button>
					</Group>

					{editModes.length > 1 ? (
						<SegmentedControl
							data={editModeData}
							value={values.editMode ?? editModes[0]}
							onChange={(value) => setValues((current) => ({ ...current, editMode: value as ImageEditMode }))}
							data-testid="image-form-edit-mode"
						/>
					) : null}

					{cannotEdit ? (
						<Alert color="yellow" data-testid="image-form-edit-unsupported">
							{t("pages.images.edit.unsupported", "The selected model cannot edit images. Pick another model.")}
						</Alert>
					) : null}

					{values.editMode === "img2img" ? (
						<Stack gap={4} data-testid="image-form-strength">
							<Text size="sm" fw={500}>
								{t("pages.images.edit.strength.label", "Strength")}
							</Text>
							<Slider
								min={0.1}
								max={1}
								step={0.05}
								value={values.strength ?? defaultImageEditStrength}
								onChange={(value) => setValues((current) => ({ ...current, strength: value }))}
								thumbLabel={t("pages.images.edit.strength.label", "Strength")}
								label={(value) => value.toFixed(2)}
							/>
							<Text size="xs" c="dimmed">
								{t("pages.images.edit.strength.description", "Lower keeps more of the source; higher changes more.")}
							</Text>
						</Stack>
					) : null}
				</Stack>
			)}

			<AssistActions surface="image" existing={assistExisting} onApply={applyPromptDraft} onDiscard={noop} />

			<Textarea
				label={t("pages.images.form.prompt.label", "Prompt")}
				placeholder={promptPlaceholder}
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
					onChange={(value) =>
						setValues((current) => ({ ...current, height: typeof value === "number" ? value : current.height }))
					}
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
					onChange={(value) => typeof value === "number" && updateSampling({ steps: value })}
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
					onChange={(value) => typeof value === "number" && updateSampling({ cfgScale: value })}
					data-testid="image-form-cfg-scale"
				/>
			</Group>

			{/* Sampler/seed go two-up only from `lg`. This app overrides Mantine's breakpoints (theme.json: md = 768,
			 lg = 1024), and 768 is exactly the width where a half-width column left the sampler Select too narrow for its
			 longest option — "Euler a" rendered as "Eule". Numbers survive a half-width column; a name does not. */}
			<SimpleGrid cols={{ base: 1, lg: 2 }} spacing="md" verticalSpacing="xs" data-testid="image-form-sampler-row">
				<Select
					label={t("pages.images.form.sampler.label", "Sampler")}
					data={samplerData}
					value={values.sampler}
					allowDeselect={false}
					error={fieldError(errors, "sampler")}
					onChange={(value) => value && updateSampling({ sampler: value as ImageGenerationFormValues["sampler"] })}
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
			</SimpleGrid>

			{hasOverride ? (
				<Group justify="flex-end">
					<Button variant="subtle" size="xs" onClick={resetSampling} data-testid="image-form-reset-sampling">
						{t("pages.images.form.resetSampling", "Reset to model defaults")}
					</Button>
				</Group>
			) : null}

			{!hasModels ? (
				<Alert color="yellow" data-testid="image-form-no-models">
					{t("pages.images.form.noModels", "Install an image model below before generating.")}
				</Alert>
			) : null}

			{submitError ? <InlineErrorAlert message={submitError} data-testid="image-form-submit-error" /> : null}

			<Group justify="flex-end">
				<Button
					leftSection={<IconSparkles size={16} />}
					loading={isSubmitting}
					disabled={!hasModels || cannotEdit}
					onClick={handleSubmit}
					data-testid="image-form-submit"
				>
					{t("pages.images.form.submit", "Generate")}
				</Button>
			</Group>
		</Stack>
	);
}
