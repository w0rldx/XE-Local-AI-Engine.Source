import { Alert, Button, Group, MultiSelect, NumberInput, Select, Stack, Switch, Text, Textarea, TextInput } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useToolCatalog } from "@/core/tools/queries/useToolCatalog";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";
import { SampleKindRow } from "@/features/training/components/DefinitionEditorDialog/SampleKindRow";
import {
	type DefinitionDraft,
	emptyDraft,
	firstDraftError,
	maxHoldoutPercent,
	minHoldoutPercent,
	newKind,
	type SampleKindDraft,
	toBody,
	toDraft,
} from "@/features/training/models/DefinitionEditorModels";
import type { TeacherOutputMode, TrainingDefinition } from "@/features/training/models/TrainingModels";
import {
	useCreateTrainingDefinition,
	useTeacherModelNames,
	useUpdateTrainingDefinition,
} from "@/features/training/queries/useTrainingDatasets";

interface DefinitionEditorDialogProps {
	opened: boolean;
	/** The definition being edited, or null to author a new one. */
	definition: TrainingDefinition | null;
	onClose: () => void;
}

/**
 * Authors a dataset definition. Everything here is a *request*: the server validates the same bounds, refuses a cloud
 * teacher, and re-snapshots the tool schemas from the live catalog at save time — so this form sends tool names only
 * and never tries to compose a snapshot of its own.
 */
export function DefinitionEditorDialog({ opened, definition, onClose }: DefinitionEditorDialogProps) {
	const { t } = useTranslation();
	const [draft, setDraft] = useState<DefinitionDraft>(emptyDraft);

	const modelNames = useTeacherModelNames();
	const catalogQuery = useToolCatalog();
	const createMutation = useCreateTrainingDefinition();
	const updateMutation = useUpdateTrainingDefinition();

	// Reopening resets the form to whatever it is opening on, so an abandoned edit never bleeds into the next one.
	useEffect(() => {
		if (opened) {
			setDraft(definition == null ? emptyDraft() : toDraft(definition));
		}
	}, [opened, definition]);

	const errorKey = firstDraftError(draft);
	const pending = createMutation.isPending || updateMutation.isPending;
	// Written out as literal keys rather than composed from `errorKey`: i18next's typed `t` only resolves the
	// "key + English default" overload for a literal key, and the English source belongs next to the key.
	const errorMessages: Record<string, string> = {
		name: t("training.definitions.editor.errors.name", "A definition needs a name."),
		teacher: t("training.definitions.editor.errors.teacher", "Pick a node-local teacher model."),
		holdout: t("training.definitions.editor.errors.holdout", "The hold-out share must be between 5 and 30 percent."),
		temperature: t("training.definitions.editor.errors.temperature", "The teacher temperature must be between 0 and 2."),
		sampleKinds: t("training.definitions.editor.errors.sampleKinds", "A definition needs at least one sample kind."),
		sampleKind: t("training.definitions.editor.errors.sampleKind", "A sample kind needs a name of at most 64 characters."),
		sampleCount: t("training.definitions.editor.errors.sampleCount", "A sample kind needs a whole target count of at least 1."),
		totalSamples: t("training.definitions.editor.errors.totalSamples", "A definition may target at most 2000 samples in total."),
		duplicateKind: t("training.definitions.editor.errors.duplicateKind", "Sample kinds must be unique per label."),
		baseSeed: t("training.definitions.editor.errors.baseSeed", "The base seed must be a whole number."),
		criticModel: t("training.definitions.editor.errors.criticModel", "Pick a critic model, or turn the critic off."),
	};

	// A tool the definition already names but the live catalog no longer offers stays selectable so it can be removed;
	// leaving it out would silently drop it on the next save.
	const catalogNames = (catalogQuery.data ?? []).map((tool) => tool.name);
	const toolOptions = [...new Set([...catalogNames, ...draft.toolNames])];

	const patch = (change: Partial<DefinitionDraft>): void => setDraft((current) => ({ ...current, ...change }));
	const patchKind = (index: number, change: Partial<SampleKindDraft>): void =>
		setDraft((current) => ({
			...current,
			sampleKinds: current.sampleKinds.map((kind, position) => (position === index ? { ...kind, ...change } : kind)),
		}));

	const submit = (): void => {
		if (errorKey != null) {
			return;
		}
		const onError = (error: unknown): void =>
			toast.error(apiErrorMessage(error, t("training.definitions.editor.saveFailed", "Could not save the dataset definition.")));
		const body = toBody(draft);
		if (definition == null) {
			createMutation.mutate({ name: draft.name.trim(), body }, { onSuccess: onClose, onError });
			return;
		}
		updateMutation.mutate(
			{ definitionId: definition.id, expectedVersion: definition.version, name: draft.name.trim(), body },
			{ onSuccess: onClose, onError },
		);
	};

	return (
		<DialogShell
			data-testid="training-definition-editor"
			onClose={onClose}
			opened={opened}
			size="lg"
			title={
				definition == null
					? t("training.definitions.editor.createTitle", "New dataset definition")
					: t("training.definitions.editor.editTitle", "Edit dataset definition")
			}
		>
			<Stack gap="md">
				<TextInput
					data-testid="training-definition-name"
					label={t("training.definitions.editor.name", "Name")}
					onChange={(event) => patch({ name: event.currentTarget.value })}
					value={draft.name}
				/>

				<Select
					data={modelNames}
					data-testid="training-definition-teacher"
					description={t("training.definitions.editor.teacherHint", "Node-local models only. Cloud teachers are refused.")}
					label={t("training.definitions.editor.teacher", "Teacher model")}
					onChange={(value) => patch({ teacherModelName: value ?? "" })}
					searchable={true}
					value={draft.teacherModelName === "" ? null : draft.teacherModelName}
				/>

				<Select
					allowDeselect={false}
					data={[
						{ value: "Constrained", label: t("training.definitions.editor.mode.Constrained", "Constrained decoding") },
						{ value: "ValidateAfter", label: t("training.definitions.editor.mode.ValidateAfter", "Validate after generation") },
					]}
					label={t("training.definitions.editor.outputMode", "Teacher output mode")}
					onChange={(value) => patch({ teacherOutputMode: (value ?? "Constrained") as TeacherOutputMode })}
					value={draft.teacherOutputMode}
				/>

				<Textarea
					label={t("training.definitions.editor.systemInstructions", "System instructions")}
					onChange={(event) => patch({ systemInstructions: event.currentTarget.value })}
					rows={4}
					value={draft.systemInstructions}
				/>

				<MultiSelect
					data={toolOptions}
					data-testid="training-definition-tools"
					description={t(
						"training.definitions.editor.toolsHint",
						"The tools the teacher is offered. The node snapshots their schemas on save.",
					)}
					label={t("training.definitions.editor.tools", "Tools")}
					onChange={(value) => patch({ toolNames: value })}
					searchable={true}
					value={draft.toolNames}
				/>

				<Stack gap="xs">
					<Group justify="space-between">
						<Text fw={500} size="sm">
							{t("training.definitions.editor.sampleKinds", "Sample kinds")}
						</Text>
						<Button
							data-testid="training-definition-add-kind"
							onClick={() => patch({ sampleKinds: [...draft.sampleKinds, newKind()] })}
							size="compact-xs"
							variant="light"
						>
							{t("training.definitions.editor.addKind", "Add kind")}
						</Button>
					</Group>
					{draft.sampleKinds.map((kind, index) => (
						<SampleKindRow
							key={kind.id}
							kind={kind}
							onChange={(change) => patchKind(index, change)}
							onRemove={() => patch({ sampleKinds: draft.sampleKinds.filter((_, position) => position !== index) })}
							removeDisabled={draft.sampleKinds.length === 1}
						/>
					))}
				</Stack>

				<Group grow={true}>
					<NumberInput
						data-testid="training-definition-holdout"
						label={t("training.definitions.editor.holdout", "Hold-out (%)")}
						max={maxHoldoutPercent}
						min={minHoldoutPercent}
						onChange={(value) => patch({ holdoutPercent: typeof value === "number" ? value : 0 })}
						value={draft.holdoutPercent}
					/>
					<NumberInput
						data-testid="training-definition-temperature"
						decimalScale={2}
						label={t("training.definitions.editor.temperature", "Teacher temperature")}
						max={2}
						min={0}
						onChange={(value) => patch({ temperature: typeof value === "number" ? value : -1 })}
						step={0.1}
						value={draft.temperature}
					/>
				</Group>

				<TextInput
					data-testid="training-definition-seed"
					description={t(
						"training.definitions.editor.baseSeedHint",
						"Optional. A 64-bit integer; leave empty for no seed policy.",
					)}
					label={t("training.definitions.editor.baseSeed", "Base seed")}
					onChange={(event) => patch({ baseSeed: event.currentTarget.value })}
					value={draft.baseSeed}
				/>

				<Switch
					checked={draft.criticEnabled}
					data-testid="training-definition-critic"
					label={t("training.definitions.editor.critic", "Score every sample with a critic model")}
					onChange={(event) => patch({ criticEnabled: event.currentTarget.checked })}
				/>
				{draft.criticEnabled ? (
					<Select
						data={modelNames}
						label={t("training.definitions.editor.criticModel", "Critic model")}
						onChange={(value) => patch({ criticModelName: value ?? "" })}
						searchable={true}
						value={draft.criticModelName === "" ? null : draft.criticModelName}
					/>
				) : null}

				{errorKey == null ? null : (
					<Alert color="yellow" data-testid="training-definition-error" icon={<IconAlertTriangle size={16} />}>
						{errorMessages[errorKey]}
					</Alert>
				)}

				<Group justify="flex-end">
					<Button onClick={onClose} variant="default">
						{t("training.definitions.editor.cancel", "Cancel")}
					</Button>
					<Button data-testid="training-definition-submit" disabled={errorKey != null} loading={pending} onClick={submit}>
						{t("training.definitions.editor.save", "Save definition")}
					</Button>
				</Group>
			</Stack>
		</DialogShell>
	);
}
