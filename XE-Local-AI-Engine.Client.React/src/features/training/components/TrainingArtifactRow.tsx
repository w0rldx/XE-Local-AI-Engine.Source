import { Badge, Button, Group, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { TrainingArtifactView } from "@/features/training/models/TrainingModels";
import {
	canDiscardArtifactQuality,
	canOverrideArtifactQuality,
	canPromote,
	canRetryArtifactDiscardCleanup,
	canValidateArtifact,
	formatBytes,
	shortDigest,
} from "@/features/training/models/TrainingModels";

const smokeColors: Record<string, string> = { Pending: "gray", Passed: "green", Failed: "red", Skipped: "yellow" };
const qualityColors: Record<string, string> = { Pending: "gray", Passed: "green", Failed: "red", Overridden: "yellow" };

interface TrainingArtifactRowActions {
	readonly smoke: (artifact: TrainingArtifactView) => void;
	readonly validate: (artifact: TrainingArtifactView) => void;
	readonly override: (artifact: TrainingArtifactView) => void;
	readonly discard: (artifact: TrainingArtifactView) => void;
	readonly retryCleanup: (artifact: TrainingArtifactView) => void;
	readonly promote: (artifact: TrainingArtifactView) => void;
	readonly remove: (artifact: TrainingArtifactView) => void;
}
interface Props {
	readonly artifact: TrainingArtifactView;
	readonly actions: TrainingArtifactRowActions;
	readonly pending: {
		readonly smoke: boolean;
		readonly discard: boolean;
		readonly remove: boolean;
		readonly revalidatingId: string | null;
	};
}
export function TrainingArtifactRow({ artifact, actions, pending }: Props) {
	const { t } = useTranslation();
	return (
		<Group gap="sm" wrap="wrap">
			<Badge color={smokeColors[artifact.smokeState] ?? "gray"} variant="light">
				{t(`training.artifacts.smoke.${artifact.smokeState}`, artifact.smokeState)}
			</Badge>
			<Badge color={qualityColors[artifact.qualityOutcome] ?? "gray"} variant="light">
				{t(`training.artifacts.quality.${artifact.qualityOutcome}`, artifact.qualityOutcome)}
			</Badge>
			{artifact.discardedAtUtc == null ? null : (
				<Badge color="red" variant="light">
					{t("training.artifacts.discarded", "Discarded")}
				</Badge>
			)}
			{artifact.discardCleanupPending ? (
				<Badge color="orange" variant="light">
					{t("training.artifacts.cleanupPending", "Cleanup pending")}
				</Badge>
			) : null}
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
				disabled={artifact.committedModelName != null || artifact.discardedAtUtc != null}
				loading={pending.smoke}
				onClick={() => actions.smoke(artifact)}
				size="compact-xs"
				variant="subtle"
			>
				{t("training.artifacts.retestSmoke", "Re-run smoke test")}
			</Button>
			<Button
				disabled={!canValidateArtifact(artifact)}
				loading={pending.revalidatingId === artifact.id}
				onClick={() => actions.validate(artifact)}
				size="compact-xs"
				variant="subtle"
			>
				{artifact.qualityComparisonId == null
					? t("training.artifacts.validate", "Validate quality")
					: t("training.artifacts.revalidate", "Revalidate quality")}
			</Button>
			{canOverrideArtifactQuality(artifact) ? (
				<Button onClick={() => actions.override(artifact)} size="compact-xs" variant="subtle">
					{t("training.artifacts.override", "Override failure")}
				</Button>
			) : null}
			{canDiscardArtifactQuality(artifact) ? (
				<Button color="red" onClick={() => actions.discard(artifact)} size="compact-xs" variant="subtle">
					{t("training.artifacts.discard", "Discard staged file")}
				</Button>
			) : null}
			{canRetryArtifactDiscardCleanup(artifact) ? (
				<Button
					color="orange"
					loading={pending.discard}
					onClick={() => actions.retryCleanup(artifact)}
					size="compact-xs"
					variant="subtle"
				>
					{t("training.artifacts.retryCleanup", "Retry cleanup")}
				</Button>
			) : null}
			<Button
				disabled={!canPromote(artifact) || pending.revalidatingId === artifact.id}
				onClick={() => actions.promote(artifact)}
				size="compact-xs"
				variant="subtle"
			>
				{t("training.artifacts.promote", "Register as model")}
			</Button>
			<Button
				color="red"
				disabled={artifact.committedModelName != null || artifact.discardedAtUtc != null}
				loading={pending.remove}
				onClick={() => actions.remove(artifact)}
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
			{artifact.discardReason == null ? null : (
				<Text c="dimmed" size="xs" w="100%">
					{artifact.discardReason}
				</Text>
			)}
		</Group>
	);
}
