import { Alert, Anchor, Badge, Group, Loader, NavLink, Paper, Select, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import {
	asDevWorkflowArtifactKind,
	decodeDevWorkflowArtifactContent,
	type DevWorkflowArtifactResponse,
	devWorkflowArtifactLanguage,
	devWorkflowArtifactLineages,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import { useDevWorkflowArtifactContent } from "@/features/devWorkflows/queries/useDevWorkflows";

export interface DevWorkflowArtifactsTabProps {
	readonly runId: string;
	readonly artifacts: readonly DevWorkflowArtifactResponse[];
}

/**
 * The run's artifacts, ONE ROW PER LINEAGE (P4 §2.8). Every version of a document used to own a permanent row, so a
 * node that re-attempted three times buried the rest of the run under its own history; the row now shows the lineage's
 * latest version and the body header offers the older ones.
 *
 * Which version a lineage shows is PER-LINEAGE state, so moving one document back a version leaves the others where
 * they were — and a `staleBecauseArtifactId` link can select a version inside a different lineage without disturbing
 * the one being read. A lineage the operator has not touched follows the wire's `isLatest` as it moves, which is what
 * makes a new version appear on the row instead of pinning the row to the version that was newest at first render.
 */
export function DevWorkflowArtifactsTab({ runId, artifacts }: DevWorkflowArtifactsTabProps) {
	const { t } = useTranslation();
	const lineages = useMemo(() => devWorkflowArtifactLineages(artifacts), [artifacts]);
	const [selectedId, setSelectedId] = useState<string | undefined>(undefined);
	const [pinnedByLineage, setPinnedByLineage] = useState<ReadonlyMap<string, string>>(new Map());

	const byId = useMemo(() => new Map(artifacts.map((artifact) => [artifact.id ?? "", artifact])), [artifacts]);
	const lineageIdByArtifactId = useMemo(() => {
		const map = new Map<string, string>();
		for (const lineage of lineages) {
			for (const version of lineage.versions) {
				map.set(version.id ?? "", lineage.lineageId);
			}
		}
		return map;
	}, [lineages]);

	/** Show one artifact, moving ITS lineage's row onto that version. Used by the rows, the picker and the stale link. */
	const selectArtifact = (artifactId: string): void => {
		const lineageId = lineageIdByArtifactId.get(artifactId);
		if (lineageId) {
			setPinnedByLineage((current) => new Map(current).set(lineageId, artifactId));
		}
		setSelectedId(artifactId);
	};

	const selected = selectedId ? byId.get(selectedId) : undefined;
	// An invalid artifact's blob is unreadable — the row already says so, and the content route would fail.
	const contentQuery = useDevWorkflowArtifactContent(runId, selected?.isValid === false ? undefined : selectedId);

	if (lineages.length === 0) {
		return (
			<EmptyState
				message={t("pages.devWorkflows.artifacts.empty", "This run has not produced any artifacts yet.")}
				data-testid="dev-workflow-artifacts-empty"
			/>
		);
	}

	const selectedLineage = selected ? lineageIdByArtifactId.get(selected.id ?? "") : undefined;

	return (
		<Stack gap="sm" data-testid="dev-workflow-artifacts-tab">
			<Stack gap={2}>
				{lineages.map((lineage) => {
					const pinnedId = pinnedByLineage.get(lineage.lineageId);
					const artifact = (pinnedId ? byId.get(pinnedId) : undefined) ?? lineage.latest;
					const artifactId = artifact.id ?? "";
					const kind = asDevWorkflowArtifactKind(artifact.kind);
					return (
						<NavLink
							key={lineage.lineageId}
							active={artifactId === selectedId}
							onClick={() => selectArtifact(artifactId)}
							data-testid={`dev-workflow-artifact-${artifactId}`}
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
										<Badge size="xs" variant="light" color="orange" data-testid={`dev-workflow-artifact-stale-${artifactId}`}>
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

			{selected ? (
				<ArtifactBody
					artifact={selected}
					versions={lineages.find((lineage) => lineage.lineageId === selectedLineage)?.versions ?? [selected]}
					supersededBy={selected.staleBecauseArtifactId ? byId.get(selected.staleBecauseArtifactId) : undefined}
					contentQuery={contentQuery}
					onSelectArtifact={selectArtifact}
				/>
			) : null}
		</Stack>
	);
}

function ArtifactBody({
	artifact,
	versions,
	supersededBy,
	contentQuery,
	onSelectArtifact,
}: {
	artifact: DevWorkflowArtifactResponse;
	/** This lineage's versions, newest first. A single-version lineage renders no picker at all. */
	versions: readonly DevWorkflowArtifactResponse[];
	/** The artifact that superseded this one's input (X6), when it is in this run's feed. */
	supersededBy?: DevWorkflowArtifactResponse;
	contentQuery: ReturnType<typeof useDevWorkflowArtifactContent>;
	onSelectArtifact: (artifactId: string) => void;
}) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs">
			<Group gap="xs" wrap="wrap" data-testid="dev-workflow-artifact-header">
				{/* A lineage with one version shows the badge alone: a picker whose only option is the value already on
				    screen is a control that does nothing — the same reason there is no regenerate button on a stale row. */}
				{versions.length > 1 ? (
					<Select
						size="xs"
						w={220}
						aria-label={t("pages.devWorkflows.artifacts.versionLabel", "Version")}
						data={versions.map((version) => ({
							value: version.id ?? "",
							label:
								version.isLatest === true
									? t("pages.devWorkflows.artifacts.versionCurrent", "v{{version}} (current)", { version: version.version ?? 1 })
									: t("pages.devWorkflows.artifacts.version", "v{{version}}", { version: version.version ?? 1 }),
						}))}
						value={artifact.id ?? ""}
						allowDeselect={false}
						onChange={(value) => {
							if (value) {
								onSelectArtifact(value);
							}
						}}
						data-testid="dev-workflow-artifact-version"
					/>
				) : (
					<Badge size="sm" variant="light" color="gray" data-testid="dev-workflow-artifact-version-badge">
						{t("pages.devWorkflows.artifacts.version", "v{{version}}", { version: artifact.version ?? 1 })}
					</Badge>
				)}
				{artifact.isStale ? (
					<Badge size="sm" variant="light" color="orange" data-testid="dev-workflow-artifact-stale-header">
						{t("pages.devWorkflows.artifacts.stale", "Stale")}
					</Badge>
				) : null}
				{/* Mark-only (O8): the link names WHAT superseded this document's input and nothing offers to redo the work,
				    because the runtime does not model that. Staleness is run-scoped in v1 (X6), so the target is in this
				    same feed — and when it is not, no link is rendered rather than one that resolves to nothing. */}
				{supersededBy ? (
					<Anchor
						component="button"
						type="button"
						size="xs"
						onClick={() => onSelectArtifact(supersededBy.id ?? "")}
						data-testid="dev-workflow-artifact-stale-because"
					>
						{t("pages.devWorkflows.artifacts.staleBecause", "stale because {{name}} v{{version}}", {
							name: supersededBy.name ?? "",
							version: supersededBy.version ?? 1,
						})}
					</Anchor>
				) : null}
			</Group>
			<ArtifactContent artifact={artifact} contentQuery={contentQuery} />
		</Stack>
	);
}

function ArtifactContent({
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
