import { Badge, Button, Group, Select, Stack, Text, TextInput } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";
import type { TrainingArtifactKindValue, TrainingArtifactView } from "@/features/training/models/TrainingModels";
import { canPromote, formatBytes, isExportedArtifact, shortDigest } from "@/features/training/models/TrainingModels";
import {
	useDeleteTrainingArtifact,
	usePromoteTrainingArtifact,
	useRunTrainingArtifactSmoke,
	useStartTrainingExport,
	useTrainingArtifacts,
} from "@/features/training/queries/useTrainingArtifacts";

// Mirrors TrainingExportQuantizations on the backend. A value outside this set is refused there, so offering one here
// would only produce a 400 the operator cannot act on.
const quantizations = ["Q4_K_M", "Q5_K_M", "Q6_K", "Q8_0", "F16"] as const;

const smokeColors: Record<string, string> = {
	Pending: "gray",
	Passed: "green",
	Failed: "red",
	Skipped: "yellow",
};

interface TrainingArtifactPanelProps {
	readonly runId: string;
	/** The export pipeline's current phase from the run hub, or null when nothing is running for this run. */
	readonly exportPhase: string | null;
	/** Tells the list which run now owns the hub subscription — a finished run has no status of its own to watch. */
	readonly onExportStarted: () => void;
}

/**
 * One finished run's staged exports: what was produced, whether it passed the smoke gate, and the two things an
 * operator can do with it — register it as a local model, or throw it away. Staged is inert by construction, so
 * nothing here is destructive until Promote is pressed.
 */
export function TrainingArtifactPanel({ runId, exportPhase, onExportStarted }: TrainingArtifactPanelProps) {
	const { t } = useTranslation();
	const [exportOpen, setExportOpen] = useState(false);
	const [kind, setKind] = useState<TrainingArtifactKindValue>("MergedGguf");
	const [quantType, setQuantType] = useState<string>(quantizations[0]);
	const [promoting, setPromoting] = useState<TrainingArtifactView | null>(null);
	const [modelName, setModelName] = useState("");

	const exporting = exportPhase != null;
	const artifactsQuery = useTrainingArtifacts(runId, exporting);
	const artifacts = (artifactsQuery.data ?? []).filter(isExportedArtifact);

	const startExport = useStartTrainingExport();
	const runSmoke = useRunTrainingArtifactSmoke();
	const promote = usePromoteTrainingArtifact();
	const remove = useDeleteTrainingArtifact();

	const failed = (error: unknown) => toast.error(apiErrorMessage(error, t("training.errors.request", "The training request failed.")));

	return (
		<Stack gap="xs" pl="md">
			<Group gap="sm">
				<Button
					disabled={exporting}
					loading={startExport.isPending}
					onClick={() => setExportOpen(true)}
					size="compact-sm"
					variant="light"
				>
					{t("training.artifacts.export", "Export")}
				</Button>
				{exportPhase == null ? null : (
					<Text c="dimmed" size="sm">
						{t(`training.artifacts.phase.${exportPhase}`, exportPhase)}
					</Text>
				)}
			</Group>

			{artifacts.map((artifact) => (
				<Group gap="sm" key={artifact.id} wrap="wrap">
					<Badge color={smokeColors[artifact.smokeState] ?? "gray"} variant="light">
						{t(`training.artifacts.smoke.${artifact.smokeState}`, artifact.smokeState)}
					</Badge>
					<Text size="sm">{artifact.fileName}</Text>
					<Text c="dimmed" size="xs">
						{formatBytes(artifact.sizeBytes)}
						{artifact.sha256 == null ? "" : ` · ${shortDigest(artifact.sha256)}`}
					</Text>
					{artifact.committedModelName == null ? null : (
						<Badge color="blue" variant="light">
							{artifact.committedModelName}
						</Badge>
					)}
					<Button
						disabled={artifact.committedModelName != null}
						loading={runSmoke.isPending}
						onClick={() =>
							runSmoke.mutate(
								{ path: { artifactId: artifact.id } },
								{ onError: failed },
							)
						}
						size="compact-xs"
						variant="subtle"
					>
						{t("training.artifacts.retestSmoke", "Re-run smoke test")}
					</Button>
					<Button
						disabled={!canPromote(artifact)}
						onClick={() => {
							setPromoting(artifact);
							setModelName("");
						}}
						size="compact-xs"
						variant="subtle"
					>
						{t("training.artifacts.promote", "Register as model")}
					</Button>
					<Button
						color="red"
						disabled={artifact.committedModelName != null}
						loading={remove.isPending}
						onClick={() =>
							remove.mutate(
								{ path: { artifactId: artifact.id }, body: { expectedVersion: artifact.version } },
								{ onError: failed },
							)
						}
						size="compact-xs"
						variant="subtle"
					>
						{t("training.artifacts.delete", "Delete")}
					</Button>
					{artifact.smokeReason == null ? null : (
						<Text c="dimmed" size="xs" w="100%">
							{artifact.smokeReason}
						</Text>
					)}
				</Group>
			))}

			<DialogShell onClose={() => setExportOpen(false)} opened={exportOpen} title={t("training.artifacts.exportTitle", "Export this run")}>
				<Stack gap="sm">
					<Select
						data={[
							{ value: "MergedGguf", label: t("training.artifacts.kind.MergedGguf", "Merged model") },
							{ value: "AdapterGguf", label: t("training.artifacts.kind.AdapterGguf", "Adapter only") },
						]}
						label={t("training.artifacts.kindLabel", "What to export")}
						onChange={(value) => setKind((value ?? "MergedGguf") as TrainingArtifactKindValue)}
						value={kind}
					/>
					{kind === "MergedGguf" ? (
						<Select
							data={[...quantizations]}
							label={t("training.artifacts.quantLabel", "Quantization")}
							onChange={(value) => setQuantType(value ?? quantizations[0])}
							value={quantType}
						/>
					) : (
						<Text c="dimmed" size="sm">
							{t("training.artifacts.adapterNote", "An adapter is always exported at F16 and is served on top of the base model it was trained against.")}
						</Text>
					)}
					<Button
						loading={startExport.isPending}
						onClick={() =>
							startExport.mutate(
								{ path: { runId }, body: { kind, quantType: kind === "MergedGguf" ? quantType : null } },
								{
									onError: failed,
									onSuccess: () => {
										setExportOpen(false);
										onExportStarted();
									},
								},
							)
						}
					>
						{t("training.artifacts.startExport", "Start export")}
					</Button>
				</Stack>
			</DialogShell>

			<DialogShell
				onClose={() => setPromoting(null)}
				opened={promoting !== null}
				title={t("training.artifacts.promoteTitle", "Register as a local model")}
			>
				<Stack gap="sm">
					<TextInput
						label={t("training.artifacts.modelNameLabel", "Model name")}
						onChange={(event) => setModelName(event.currentTarget.value)}
						placeholder={t("training.artifacts.modelNamePlaceholder", "my-tuned-model")}
						value={modelName}
					/>
					<Text c="dimmed" size="xs">
						{t("training.artifacts.promoteNote", "The quantization is appended to the name automatically, and the model records the checkpoint and dataset it came from.")}
					</Text>
					<Button
						disabled={modelName.trim().length === 0}
						loading={promote.isPending}
						onClick={() => {
							const artifact = promoting;
							if (artifact == null) {
								return;
							}
							promote.mutate(
								{ path: { artifactId: artifact.id }, body: { modelName: modelName.trim() } },
								{
									onError: failed,
									onSuccess: () => setPromoting(null),
								},
							);
						}}
					>
						{t("training.artifacts.promote", "Register as model")}
					</Button>
				</Stack>
			</DialogShell>
		</Stack>
	);
}
