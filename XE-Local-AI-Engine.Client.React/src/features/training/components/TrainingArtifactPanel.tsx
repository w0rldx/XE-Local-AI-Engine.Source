import { Button, Group, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import { ComparisonCreateDialog } from "@/features/training/components/ComparisonCreateDialog";
import { TrainingArtifactExportDialog } from "@/features/training/components/TrainingArtifactExportDialog";
import { TrainingArtifactRow } from "@/features/training/components/TrainingArtifactRow";
import { TrainingArtifactTextDialog } from "@/features/training/components/TrainingArtifactTextDialog";
import type { TrainingArtifactKindValue, TrainingArtifactView } from "@/features/training/models/TrainingModels";
import { defaultTrainingExportQuantization, isExportedArtifact } from "@/features/training/models/TrainingModels";
import {
	useBeginTrainingArtifactQualityRevalidation,
	useDecideTrainingArtifactQuality,
	useDeleteTrainingArtifact,
	useDiscardTrainingArtifactQuality,
	useOverrideTrainingArtifactQuality,
	usePromoteTrainingArtifact,
	useRunTrainingArtifactSmoke,
	useStartTrainingExport,
	useTrainingArtifacts,
} from "@/features/training/queries/useTrainingArtifacts";

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
	const [quantType, setQuantType] = useState<string>(defaultTrainingExportQuantization);
	const [promoting, setPromoting] = useState<TrainingArtifactView | null>(null);
	const [modelName, setModelName] = useState("");
	const [validating, setValidating] = useState<TrainingArtifactView | null>(null);
	const [overriding, setOverriding] = useState<TrainingArtifactView | null>(null);
	const [overrideReason, setOverrideReason] = useState("");
	const [discarding, setDiscarding] = useState<TrainingArtifactView | null>(null);
	const [discardReason, setDiscardReason] = useState("");
	const [revalidatingArtifactId, setRevalidatingArtifactId] = useState<string | null>(null);

	const exporting = exportPhase != null;
	const artifactsQuery = useTrainingArtifacts(runId, exporting);
	const artifacts = (artifactsQuery.data ?? []).filter(isExportedArtifact);

	const startExport = useStartTrainingExport();
	const runSmoke = useRunTrainingArtifactSmoke();
	const promote = usePromoteTrainingArtifact();
	const remove = useDeleteTrainingArtifact();
	const decideQuality = useDecideTrainingArtifactQuality();
	const beginRevalidation = useBeginTrainingArtifactQualityRevalidation();
	const overrideQuality = useOverrideTrainingArtifactQuality();
	const discardQuality = useDiscardTrainingArtifactQuality();

	const failed = (error: unknown) =>
		toast.error(apiErrorMessage(error, t("training.errors.request", "The training request failed.")));
	const validateArtifact = (artifact: TrainingArtifactView) => {
		if (artifact.qualityComparisonId == null || artifact.qualityOutcome === "Pending") {
			setValidating(artifact);
			return;
		}
		setRevalidatingArtifactId(artifact.id);
		beginRevalidation.mutate(
			{ path: { artifactId: artifact.id }, body: { expectedVersion: artifact.version } },
			{
				onError: (error) => {
					setRevalidatingArtifactId(null);
					failed(error);
				},
				onSuccess: (response) => {
					setRevalidatingArtifactId(null);
					setValidating({ ...artifact, version: response.version, qualityOutcome: "Pending" });
				},
			},
		);
	};

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
				<TrainingArtifactRow
					actions={{
						discard: (selected) => {
							setDiscarding(selected);
							setDiscardReason("");
						},
						override: (selected) => {
							setOverriding(selected);
							setOverrideReason("");
						},
						promote: (selected) => {
							setPromoting(selected);
							setModelName("");
						},
						remove: (selected) =>
							remove.mutate(
								{ path: { artifactId: selected.id }, body: { expectedVersion: selected.version } },
								{ onError: failed },
							),
						retryCleanup: (selected) =>
							discardQuality.mutate(
								{
									path: { artifactId: selected.id },
									body: { expectedVersion: selected.version, reason: selected.discardReason ?? "" },
								},
								{ onError: failed },
							),
						smoke: (selected) => runSmoke.mutate({ path: { artifactId: selected.id } }, { onError: failed }),
						validate: validateArtifact,
					}}
					artifact={artifact}
					key={artifact.id}
					pending={{
						discard: discardQuality.isPending,
						remove: remove.isPending,
						revalidatingId: revalidatingArtifactId,
						smoke: runSmoke.isPending,
					}}
				/>
			))}

			<TrainingArtifactExportDialog
				opened={exportOpen}
				kind={kind}
				quantType={quantType}
				isPending={startExport.isPending}
				onClose={() => setExportOpen(false)}
				onKindChange={setKind}
				onQuantTypeChange={setQuantType}
				onStart={() =>
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
			/>

			<ComparisonCreateDialog
				artifactId={validating?.id}
				freshEvaluations={validating?.qualityOutcome === "Pending" && validating?.qualityComparisonId != null}
				initialRunId={validating?.runId}
				onClose={() => setValidating(null)}
				onComparisonCreated={(comparisonId) => {
					const artifact = validating;
					if (artifact == null) {
						return;
					}
					decideQuality.mutate(
						{
							path: { artifactId: artifact.id },
							body: { comparisonId, expectedVersion: artifact.version },
						},
						{
							onError: failed,
							onSuccess: () => setValidating(null),
						},
					);
				}}
				opened={validating !== null}
			/>

			<TrainingArtifactTextDialog
				kind="override"
				onClose={() => setOverriding(null)}
				opened={overriding !== null}
				pending={overrideQuality.isPending}
				onChange={setOverrideReason}
				onConfirm={() => {
					if (overriding == null) {
						return;
					}
					overrideQuality.mutate(
						{ path: { artifactId: overriding.id }, body: { expectedVersion: overriding.version, reason: overrideReason.trim() } },
						{ onError: failed, onSuccess: () => setOverriding(null) },
					);
				}}
				value={overrideReason}
			/>
			<TrainingArtifactTextDialog
				kind="discard"
				onClose={() => setDiscarding(null)}
				opened={discarding !== null}
				pending={discardQuality.isPending}
				onChange={setDiscardReason}
				onConfirm={() => {
					if (discarding == null) {
						return;
					}
					discardQuality.mutate(
						{ path: { artifactId: discarding.id }, body: { expectedVersion: discarding.version, reason: discardReason.trim() } },
						{ onError: failed, onSuccess: () => setDiscarding(null) },
					);
				}}
				value={discardReason}
			/>
			<TrainingArtifactTextDialog
				kind="promote"
				onClose={() => setPromoting(null)}
				opened={promoting !== null}
				pending={promote.isPending}
				onChange={setModelName}
				onConfirm={() => {
					if (promoting == null) {
						return;
					}
					promote.mutate(
						{ path: { artifactId: promoting.id }, body: { modelName: modelName.trim() } },
						{ onError: failed, onSuccess: () => setPromoting(null) },
					);
				}}
				value={modelName}
			/>
		</Stack>
	);
}
