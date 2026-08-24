import { Alert, Badge, Group, Loader, NavLink, Paper, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import {
	artifactEditorLanguage,
	decodeArtifactContent,
	toWorkSessionArtifactKind,
	type WorkSessionArtifactResponse,
} from "@/features/workSessions/models/WorkSessionModels";
import { useWorkSessionArtifactContent } from "@/features/workSessions/queries/useWorkSessions";

export interface WorkSessionArtifactsTabProps {
	readonly sessionId: string;
	readonly artifacts: readonly WorkSessionArtifactResponse[];
	/** A finished session opens on its report rather than making the operator hunt for it. */
	readonly preselectReport: boolean;
}

export function WorkSessionArtifactsTab({ sessionId, artifacts, preselectReport }: WorkSessionArtifactsTabProps) {
	const { t } = useTranslation();
	const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

	const reportId = preselectReport
		? artifacts.find((artifact) => toWorkSessionArtifactKind(artifact.kind) === "Report")?.id
		: undefined;
	useEffect(() => {
		if (reportId) {
			setSelectedId((current) => current ?? reportId);
		}
	}, [reportId]);

	const selected = artifacts.find((artifact) => artifact.id === selectedId);
	// An invalid artifact's blob is unreadable — the list row already says so, and the content route would 404.
	const contentQuery = useWorkSessionArtifactContent(sessionId, selected?.isValid === false ? undefined : selectedId);

	return (
		<Stack gap="sm" data-testid="work-session-artifacts-tab">
			{artifacts.length === 0 ? (
				<Alert color="gray" variant="light" data-testid="work-session-artifacts-empty">
					{t("pages.workSessions.artifacts.empty", "The agent has not saved any artifacts yet.")}
				</Alert>
			) : (
				<Stack gap={2}>
					{artifacts.map((artifact) => (
						<NavLink
							key={artifact.id}
							active={artifact.id === selectedId}
							onClick={() => setSelectedId(artifact.id)}
							data-testid={`work-session-artifact-${artifact.id}`}
							label={
								<Group gap="xs" wrap="nowrap">
									<Text size="sm" lineClamp={1} style={{ flex: 1, minWidth: 0 }}>
										{artifact.name}
									</Text>
									<Badge size="xs" variant="light">
										{t(`pages.workSessions.artifactKind.${toWorkSessionArtifactKind(artifact.kind)}`, artifact.kind ?? "")}
									</Badge>
									{artifact.isValid === false ? (
										<Badge size="xs" variant="light" color="red">
											{t("pages.workSessions.artifacts.invalid", "Unreadable")}
										</Badge>
									) : null}
								</Group>
							}
							description={t("pages.workSessions.artifacts.meta", "{{mediaType}} · {{bytes}} bytes · step {{step}}", {
								mediaType: artifact.mediaType ?? "",
								bytes: artifact.sizeBytes ?? 0,
								step: artifact.createdStep ?? 0,
							})}
						/>
					))}
				</Stack>
			)}

			{selected ? <ArtifactBody artifact={selected} contentQuery={contentQuery} /> : null}
		</Stack>
	);
}

function ArtifactBody({
	artifact,
	contentQuery,
}: {
	artifact: WorkSessionArtifactResponse;
	contentQuery: ReturnType<typeof useWorkSessionArtifactContent>;
}) {
	const { t } = useTranslation();

	if (artifact.isValid === false) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="work-session-artifact-invalid-alert">
				{t("pages.workSessions.artifacts.invalidBody", "This artifact's stored content could not be read and cannot be shown.")}
			</Alert>
		);
	}
	if (contentQuery.isPending) {
		return <Loader size="sm" data-testid="work-session-artifact-loading" />;
	}
	if (contentQuery.isError) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="work-session-artifact-error">
				{apiErrorMessage(contentQuery.error, t("pages.workSessions.artifacts.loadFailed", "Could not load this artifact."))}
			</Alert>
		);
	}

	const decoded = decodeArtifactContent(contentQuery.data?.content ?? "", contentQuery.data?.isBase64 === true);
	if (decoded.isBinary) {
		return (
			<Alert color="yellow" variant="light" data-testid="work-session-artifact-binary">
				{t("pages.workSessions.artifacts.binary", "This artifact is binary and has no text preview.")}
			</Alert>
		);
	}

	return (
		<Paper withBorder={true} p={0} data-testid="work-session-artifact-viewer">
			<CodeEditor
				value={decoded.text}
				language={artifactEditorLanguage(toWorkSessionArtifactKind(artifact.kind), artifact.mediaType)}
				readOnly={true}
				height={320}
				wordWrap={true}
				aria-label={artifact.name ?? "artifact"}
				data-testid="work-session-artifact-editor"
			/>
		</Paper>
	);
}
