import { Alert, Badge, Box, Card, Divider, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { KnowledgeStatusBadge } from "@/features/knowledge/components/KnowledgeStatusBadge";
import {
	type KnowledgeDocumentDetail,
	formatKnowledgeBytes,
	formatKnowledgeTimestamp,
} from "@/features/knowledge/models/KnowledgeModels";

interface KnowledgeDocumentDrawerProps {
	readonly opened: boolean;
	readonly detail: KnowledgeDocumentDetail | undefined;
	readonly isLoading: boolean;
	onClose(): void;
}

interface MetadataRowProps {
	readonly label: string;
	readonly children: ReactNode;
}

function MetadataRow({ label, children }: MetadataRowProps) {
	return (
		<Group justify="space-between" gap="md" wrap="nowrap">
			<Text size="sm" c="dimmed">
				{label}
			</Text>
			<Box fz="sm" ta="right" style={{ minWidth: 0 }}>
				{children}
			</Box>
		</Group>
	);
}

// Read-only detail drawer (DialogShell modal): document metadata + its extracted chunks (index, heading path,
// content). Content is rendered verbatim with preserved whitespace. The parent fetches the detail lazily (only
// while open) and passes it in.
export function KnowledgeDocumentDrawer({ opened, detail, isLoading, onClose }: KnowledgeDocumentDrawerProps) {
	const { t } = useTranslation();

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={detail?.displayName ?? t("pages.knowledgeBase.detail.title", "Document")}
		>
			<Stack gap="md" py="xs" data-testid="knowledge-detail">
				{isLoading && !detail ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.knowledgeBase.detail.loading", "Loading document…")}</Text>
					</Group>
				) : null}

				{detail ? (
					<>
						<Stack gap="xs">
							<MetadataRow label={t("pages.knowledgeBase.table.status", "Status")}>
								<KnowledgeStatusBadge status={detail.status} />
							</MetadataRow>
							<MetadataRow label={t("pages.knowledgeBase.table.chunks", "Chunks")}>{detail.chunkCount}</MetadataRow>
							<MetadataRow label={t("pages.knowledgeBase.table.embeddingModel", "Embedding model")}>
								<Group gap={6} justify="flex-end" wrap="nowrap">
									<Text size="sm">{detail.embeddingModel || "—"}</Text>
									{detail.staleModel ? (
										<Badge color="orange" variant="light" size="xs">
											{t("pages.knowledgeBase.table.stale", "Stale")}
										</Badge>
									) : null}
								</Group>
							</MetadataRow>
							<MetadataRow label={t("pages.knowledgeBase.table.size", "Size")}>
								{formatKnowledgeBytes(detail.sizeBytes)}
							</MetadataRow>
							<MetadataRow label={t("pages.knowledgeBase.table.created", "Created")}>
								{formatKnowledgeTimestamp(detail.createdAtUtc)}
							</MetadataRow>
							<MetadataRow label={t("pages.knowledgeBase.detail.updated", "Updated")}>
								{formatKnowledgeTimestamp(detail.updatedAtUtc)}
							</MetadataRow>
						</Stack>

						{detail.status === "Failed" && detail.failureReason ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />}>
								{detail.failureReason}
							</Alert>
						) : null}

						<Divider label={t("pages.knowledgeBase.detail.chunks", "Chunks")} labelPosition="left" />

						{detail.chunks.length > 0 ? (
							<Stack gap="sm">
								{detail.chunks.map((chunk) => (
									<Card key={chunk.chunkIndex} withBorder={true} radius="sm" p="sm">
										<Stack gap={6}>
											<Group gap="xs" justify="space-between" wrap="nowrap">
												<Badge variant="outline" color="gray" size="sm">
													{t("pages.knowledgeBase.detail.chunkLabel", "Chunk {{index}}", { index: chunk.chunkIndex })}
												</Badge>
												{chunk.headingPath ? (
													<Text size="xs" c="dimmed" truncate="end">
														{chunk.headingPath}
													</Text>
												) : null}
											</Group>
											<Text size="sm" style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>
												{chunk.content}
											</Text>
										</Stack>
									</Card>
								))}
							</Stack>
						) : (
							<Text c="dimmed" size="sm">
								{t("pages.knowledgeBase.detail.noChunks", "This document has no indexed chunks yet.")}
							</Text>
						)}
					</>
				) : null}
			</Stack>
		</DialogShell>
	);
}
