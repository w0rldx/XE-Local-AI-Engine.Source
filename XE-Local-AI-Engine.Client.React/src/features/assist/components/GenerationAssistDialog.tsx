import { Alert, Button, Collapse, Group, List, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconInfoCircle, IconSparkles } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { MarkdownEditorField } from "@/core/ui/components/MarkdownEditorField/MarkdownEditorField";
import {
	ASSIST_BRIEF_MAX,
	type AssistDraft,
	type AssistExistingContent,
	type AssistMode,
	type AssistSurface,
} from "@/features/assist/models/AssistModels";
import { useAssistDraft } from "@/features/assist/queries/useAssistDraft";
import { resolveLocalDefaultModelName } from "@/features/chat/pages/ChatModelOptions";

type LocalModelDto = XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse;

interface GenerationAssistDialogProps {
	opened: boolean;
	surface: AssistSurface;
	mode: AssistMode;
	/** What the parent form currently holds — the baseline an Improve draft revises. */
	existing: AssistExistingContent;
	/** Installed local chat models, already filtered by the caller. The server check stays authoritative. */
	models: readonly LocalModelDto[];
	/** Names the runtime currently holds in memory; drives the default pick and the "not loaded" note. */
	loadedModelNames: readonly string[];
	/** Hands the accepted draft to the parent form. Never saves — the existing CRUD path is the only persistence. */
	onApply: (draft: AssistDraft) => void;
	/** The operator threw the draft away: the parent must drop any provenance it was tracking. */
	onDiscard: () => void;
	onClose: () => void;
}

// Generation runs against a local model and is genuinely slow; the elapsed counter ticks once a second so a pending
// dialog never looks frozen.
const ELAPSED_TICK_MS = 1000;

/**
 * Draft (or revise) an agent's instructions / a skill's body with a node-local model.
 *
 * The dialog owns the whole generate → review → apply loop and persists nothing: Apply hands the reviewed fields and
 * the opaque provenance block to the parent form, which saves through the normal CRUD endpoint. Every failure the
 * operator can act on gets its own framing — a busy node is a notice, not an error, and unparseable model output
 * offers a retry rather than echoing whatever the model emitted.
 */
export function GenerationAssistDialog({
	opened,
	surface,
	mode,
	existing,
	models,
	loadedModelNames,
	onApply,
	onDiscard,
	onClose,
}: GenerationAssistDialogProps) {
	const { t } = useTranslation();
	const mutation = useAssistDraft(surface);

	// Null until the operator picks explicitly, so the default keeps tracking the loaded model as the poll refreshes.
	const [pickedModel, setPickedModel] = useState<string | null>(null);
	const [brief, setBrief] = useState("");
	// The generated draft, editable in place before Apply. `generationMetadata` rides along untouched.
	const [draft, setDraft] = useState<AssistDraft | null>(null);
	const [showRationale, setShowRationale] = useState(false);
	// A deliberate cancel rejects the mutation like any other failure; this flag keeps it from rendering as one.
	const [isCancelled, setIsCancelled] = useState(false);
	const [elapsedMs, setElapsedMs] = useState(0);
	const abortRef = useRef<AbortController | null>(null);

	const defaultModelName = useMemo(() => {
		const loaded = models.find((model) => loadedModelNames.includes(model.modelName ?? ""));
		return loaded?.modelName ?? resolveLocalDefaultModelName([...models]);
	}, [models, loadedModelNames]);

	const modelName = pickedModel ?? defaultModelName ?? "";
	const modelSelectData = useMemo(
		() =>
			models
				.map((model) => ({ value: model.modelName ?? "", label: model.modelName ?? "" }))
				.filter((option) => option.value.length > 0),
		[models],
	);
	const isModelLoaded = loadedModelNames.includes(modelName);

	// Abort any in-flight generation when the dialog unmounts so a closed dialog never holds the node's draft slot.
	useEffect(() => () => abortRef.current?.abort(), []);

	useEffect(() => {
		if (!mutation.isPending) {
			return;
		}

		const startedAt = Date.now();
		const timer = setInterval(() => setElapsedMs(Date.now() - startedAt), ELAPSED_TICK_MS);
		return () => clearInterval(timer);
	}, [mutation.isPending]);

	const handleGenerate = useCallback(() => {
		const controller = new AbortController();
		abortRef.current = controller;
		setIsCancelled(false);
		setDraft(null);
		setElapsedMs(0);
		mutation.mutate(
			{
				mode,
				modelName,
				brief,
				// Create sends the brief alone; Improve sends what the form already holds as the revision baseline.
				existingName: mode === "Improve" ? existing.name : undefined,
				existingDescription: mode === "Improve" ? existing.description : undefined,
				existingContent: mode === "Improve" ? existing.content : undefined,
				signal: controller.signal,
			},
			{ onSuccess: setDraft },
		);
	}, [brief, existing, mode, modelName, mutation]);

	const handleCancel = useCallback(() => {
		setIsCancelled(true);
		abortRef.current?.abort();
	}, []);

	const handleApply = useCallback(() => {
		if (draft) {
			onApply(draft);
		}
		onClose();
	}, [draft, onApply, onClose]);

	const handleDiscard = useCallback(() => {
		abortRef.current?.abort();
		setDraft(null);
		mutation.reset();
		onDiscard();
		onClose();
	}, [mutation, onDiscard, onClose]);

	// The two failures with their own operator-facing framing. A 409 means the node is busy — a wait, not a fault —
	// and a 422 means the model's output could not be parsed, which a retry or another model may fix. Everything
	// else (including the fail-closed model-eligibility 400) shows the server's own sanitized message.
	const status = mutation.error instanceof ApiError ? mutation.error.statusCode : undefined;
	const showFailure = mutation.isError && !isCancelled;

	const surfaceContentLabel =
		surface === "agent" ? t("assist.result.instructionsLabel", "Instructions") : t("assist.result.bodyLabel", "Body");

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={
				mode === "Improve" ? t("assist.dialog.improveTitle", "Improve with AI") : t("assist.dialog.draftTitle", "Draft with AI")
			}
			// Above the agent/skill editor dialog (300), below the unsaved-changes confirm (400).
			zIndex={350}
			data-testid="assist-dialog"
			footer={
				<>
					{mutation.isPending ? (
						<Button variant="light" color="red" onClick={handleCancel} data-testid="assist-cancel">
							{t("assist.actions.cancel", "Cancel generation")}
						</Button>
					) : null}
					<Button variant="subtle" onClick={handleDiscard} data-testid="assist-discard">
						{t("assist.actions.discard", "Discard")}
					</Button>
					<Button
						variant="default"
						leftSection={<IconSparkles size={16} />}
						onClick={handleGenerate}
						loading={mutation.isPending}
						disabled={brief.trim().length === 0 || modelName.length === 0}
						data-testid="assist-generate"
					>
						{draft ? t("assist.actions.regenerate", "Generate again") : t("assist.actions.generate", "Generate")}
					</Button>
					<Button onClick={handleApply} disabled={draft === null} data-testid="assist-apply">
						{t("assist.actions.apply", "Apply to form")}
					</Button>
				</>
			}
		>
			<Stack gap="md" px="md" pb="md">
				<Text size="sm" c="dimmed" data-testid="assist-intro">
					{t(
						"assist.intro",
						"A model on this node writes a draft from your description. Nothing is saved until you apply it and save the form yourself.",
					)}
				</Text>

				<Select
					label={t("assist.model.label", "Model")}
					description={t("assist.model.description", "Only chat models installed on this node can draft.")}
					data={modelSelectData}
					value={modelName.length > 0 ? modelName : null}
					onChange={setPickedModel}
					disabled={mutation.isPending}
					searchable={true}
					data-testid="assist-model"
				/>
				{modelName.length > 0 && !isModelLoaded ? (
					<Text size="xs" c="dimmed" data-testid="assist-model-not-loaded">
						{t("assist.model.notLoaded", "This model is not loaded — the first generation will load it, which takes longer.")}
					</Text>
				) : null}

				<Textarea
					label={
						mode === "Improve"
							? t("assist.brief.improveLabel", "What should change?")
							: t("assist.brief.createLabel", "What should this do?")
					}
					description={
						mode === "Improve"
							? t("assist.brief.improveDescription", "Describe the revision. The current content is sent as the starting point.")
							: t("assist.brief.createDescription", "Describe the job in your own words — the model turns it into a draft.")
					}
					placeholder={t(
						"assist.brief.placeholder",
						"Review supplier invoices against the agreed rate card and flag anything unusual.",
					)}
					value={brief}
					onChange={(event) => setBrief(event.currentTarget.value)}
					maxLength={ASSIST_BRIEF_MAX}
					autosize={true}
					minRows={3}
					disabled={mutation.isPending}
					data-testid="assist-brief"
				/>

				{mutation.isPending ? (
					<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="assist-pending">
						{t("assist.pending.elapsed", "Generating… {{seconds}}s elapsed.", {
							seconds: Math.floor(elapsedMs / 1000),
						})}{" "}
						{t("assist.pending.hint", "Local models can take several minutes. You can cancel at any time.")}
					</Alert>
				) : null}

				{showFailure && status === 409 ? (
					<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="assist-busy-notice">
						{t("assist.errors.busy", "The node is running another task right now. Try again once it finishes.")}
					</Alert>
				) : null}
				{showFailure && status === 422 ? (
					<Alert color="yellow" variant="light" data-testid="assist-unparseable">
						{t("assist.errors.unparseable", "The model did not return a usable draft. Try again, or pick a different model.")}
					</Alert>
				) : null}
				{showFailure && status !== 409 && status !== 422 ? (
					<Alert color="red" data-testid="assist-error">
						{apiErrorMessage(mutation.error, t("assist.errors.generic", "Could not generate a draft."))}
					</Alert>
				) : null}

				{draft ? (
					<Stack gap="md" data-testid="assist-result">
						<TextInput
							label={t("assist.result.nameLabel", "Name")}
							value={draft.name}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setDraft((current) => (current ? { ...current, name: value } : current));
							}}
							data-testid="assist-result-name"
						/>
						<Textarea
							label={t("assist.result.descriptionLabel", "Description")}
							value={draft.description}
							autosize={true}
							minRows={2}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setDraft((current) => (current ? { ...current, description: value } : current));
							}}
							data-testid="assist-result-description"
						/>
						<MarkdownEditorField
							label={surfaceContentLabel}
							value={draft.content}
							minRows={8}
							onChange={(value) => setDraft((current) => (current ? { ...current, content: value } : current))}
							data-testid="assist-result-content"
						/>

						<Group justify="flex-start">
							<Button
								variant="subtle"
								size="compact-sm"
								onClick={() => setShowRationale((current) => !current)}
								data-testid="assist-why-toggle"
							>
								{t("assist.rationale.toggle", "Why this draft")}
							</Button>
						</Group>
						<Collapse expanded={showRationale}>
							<Stack gap="xs" data-testid="assist-why">
								<Text size="sm">
									{draft.generationMetadata.rationale ??
										t("assist.rationale.none", "The model gave no explanation for this draft.")}
								</Text>
								{draft.generationMetadata.assumptions && draft.generationMetadata.assumptions.length > 0 ? (
									<>
										<Text size="sm" fw={600}>
											{t("assist.rationale.assumptions", "Assumptions it made")}
										</Text>
										<List size="sm">
											{draft.generationMetadata.assumptions.map((assumption) => (
												<List.Item key={assumption}>{assumption}</List.Item>
											))}
										</List>
									</>
								) : null}
								<Text size="xs" c="dimmed">
									{t("assist.rationale.confidence", "The model's own confidence: {{percent}}% — its claim, not a measurement.", {
										percent: Math.round((draft.generationMetadata.confidence ?? 0) * 100),
									})}
								</Text>
							</Stack>
						</Collapse>
					</Stack>
				) : null}
			</Stack>
		</DialogShell>
	);
}
