import { Alert, Badge, Group, Loader, NavLink, Paper, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import {
	asDevWorkflowArtifactKind,
	decodeDevWorkflowArtifactContent,
	type DevWorkflowArtifactResponse,
	devWorkflowArtifactLanguage,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import { useDevWorkflowArtifactContent } from "@/features/devWorkflows/queries/useDevWorkflows";

export interface DevWorkflowArtifactsTabProps {
	readonly runId: string;
	readonly artifacts: readonly DevWorkflowArtifactResponse[];
}

export function DevWorkflowArtifactsTab({ runId, artifacts }: DevWorkflowArtifactsTabProps) {
	const { t } = useTranslation();
	const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

	const selected = artifacts.find((artifact) => artifact.id === selectedId);
	// An invalid artifact's blob is unreadable — the row already says so, and the content route would fail.
	const contentQuery = useDevWorkflowArtifactContent(runId, selected?.isValid === false ? undefined : selectedId);

	if (artifacts.length === 0) {
		return (
			<EmptyState
				message={t("pages.devWorkflows.artifacts.empty", "This run has not produced any artifacts yet.")}
				data-testid="dev-workflow-artifacts-empty"
			/>
		);
	}

	return (
		<Stack gap="sm" data-testid="dev-workflow-artifacts-tab">
			<Stack gap={2}>
				{artifacts.map((artifact) => {
					const kind = asDevWorkflowArtifactKind(artifact.kind);
					return (
						<NavLink
							key={artifact.id}
							active={artifact.id === selectedId}
							onClick={() => setSelectedId(artifact.id)}
							data-testid={`dev-workflow-artifact-${artifact.id}`}
							label={
								<Group gap="xs" wrap="nowrap">
									<Text size="sm" lineClamp={1} style={{ flex: 1, minWidth: 0 }}>
										{artifact.name}
									</Text>
									<Badge size="xs" variant="light">
										{/* An unrecognised kind prints its own token rather than being relabelled as a known one. */}
										{kind ? t(`pages.devWorkflows.artifactKind.${kind}`, kind) : (artifact.kind ?? "")}
									</Badge>
									{artifact.isStale ? (
										<Badge size="xs" variant="light" color="orange" data-testid={`dev-workflow-artifact-stale-${artifact.id}`}>
											{t("pages.devWorkflows.artifacts.stale", "Stale")}
										</Badge>
									) : null}
									{artifact.isValid === false ? (
										<Badge size="xs" variant="light" color="red">
											{t("pages.devWorkflows.artifacts.invalid", "Unreadable")}
										</Badge>
									) : null}
								</Group>
							}
							description={t("pages.devWorkflows.artifacts.meta", "v{{version}} · from {{node}} · {{bytes}} bytes", {
								version: artifact.version ?? 1,
								node: artifact.producingNodeKey ?? "",
								bytes: artifact.sizeBytes ?? 0,
							})}
						/>
					);
				})}
			</Stack>

			{selected ? <ArtifactBody artifact={selected} contentQuery={contentQuery} /> : null}
		</Stack>
	);
}

function ArtifactBody({
	artifact,
	contentQuery,
}: {
	artifact: DevWorkflowArtifactResponse;
	contentQuery: ReturnType<typeof useDevWorkflowArtifactContent>;
}) {
	const { t } = useTranslation();

	if (artifact.isValid === false) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-artifact-invalid">
				{t("pages.devWorkflows.artifacts.invalidBody", "This artifact's stored content could not be read and cannot be shown.")}
			</Alert>
		);
	}
	if (contentQuery.isPending) {
		return <Loader size="sm" data-testid="dev-workflow-artifact-loading" />;
	}
	if (contentQuery.isError) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-artifact-error">
				{apiErrorMessage(contentQuery.error, t("pages.devWorkflows.artifacts.loadFailed", "Could not load this artifact."))}
			</Alert>
		);
	}

	const decoded = decodeDevWorkflowArtifactContent(contentQuery.data?.content ?? "", contentQuery.data?.isBase64 === true);
	if (decoded.isBinary) {
		return (
			<Alert color="yellow" variant="light" data-testid="dev-workflow-artifact-binary">
				{t("pages.devWorkflows.artifacts.binary", "This artifact is binary and has no text preview.")}
			</Alert>
		);
	}

	return (
		<Paper withBorder={true} p={0} data-testid="dev-workflow-artifact-viewer">
			<CodeEditor
				value={decoded.text}
				language={devWorkflowArtifactLanguage(asDevWorkflowArtifactKind(artifact.kind), artifact.mediaType)}
				readOnly={true}
				height={320}
				wordWrap={true}
				aria-label={artifact.name ?? "artifact"}
				data-testid="dev-workflow-artifact-editor"
			/>
		</Paper>
	);
}
