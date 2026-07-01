import { Box, Card, Group, Progress, Stack, Text } from "@mantine/core";
import { IconCloudUpload, IconFileText } from "@tabler/icons-react";
import { type ChangeEvent, type DragEvent, type KeyboardEvent as ReactKeyboardEvent, useCallback, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	KNOWLEDGE_ACCEPT_ATTRIBUTE,
	KNOWLEDGE_MAX_UPLOAD_SIZE_MB,
	formatKnowledgeBytes,
} from "@/features/knowledge/models/KnowledgeModels";
import type { KnowledgePendingUpload } from "@/features/knowledge/queries/useKnowledgeUpload";

interface KnowledgeUploadPanelProps {
	readonly pendingUploads: readonly KnowledgePendingUpload[];
	onUpload(files: readonly File[]): void;
}

// Drag-and-drop / click-to-browse ingestion surface. Dependency-free (no @mantine/dropzone in this build): a
// bordered, keyboard-focusable drop zone drives a hidden file input, with a live per-file progress list beneath.
// Purely presentational — the parent owns the upload hook and passes in the pending set + upload handler.
export function KnowledgeUploadPanel({ pendingUploads, onUpload }: KnowledgeUploadPanelProps) {
	const { t } = useTranslation();
	const inputRef = useRef<HTMLInputElement>(null);
	const [isDragging, setIsDragging] = useState(false);

	const openPicker = useCallback((): void => {
		inputRef.current?.click();
	}, []);

	const handleInputChange = useCallback(
		(event: ChangeEvent<HTMLInputElement>): void => {
			const files = event.target.files ? Array.from(event.target.files) : [];
			if (files.length > 0) {
				onUpload(files);
			}
			// Reset so selecting the same file again re-fires change.
			event.target.value = "";
		},
		[onUpload],
	);

	const handleDrop = useCallback(
		(event: DragEvent<HTMLDivElement>): void => {
			event.preventDefault();
			setIsDragging(false);
			const files = event.dataTransfer?.files ? Array.from(event.dataTransfer.files) : [];
			if (files.length > 0) {
				onUpload(files);
			}
		},
		[onUpload],
	);

	const handleDragOver = useCallback((event: DragEvent<HTMLDivElement>): void => {
		event.preventDefault();
		setIsDragging(true);
	}, []);

	const handleDragLeave = useCallback((event: DragEvent<HTMLDivElement>): void => {
		event.preventDefault();
		setIsDragging(false);
	}, []);

	const handleKeyDown = useCallback(
		(event: ReactKeyboardEvent<HTMLDivElement>): void => {
			if (event.key === "Enter" || event.key === " ") {
				event.preventDefault();
				openPicker();
			}
		},
		[openPicker],
	);

	return (
		<Stack gap="sm">
			<Box
				role="button"
				tabIndex={0}
				aria-label={t("pages.knowledgeBase.upload.dropzoneAria", "Upload documents to the knowledge base")}
				onClick={openPicker}
				onKeyDown={handleKeyDown}
				onDrop={handleDrop}
				onDragOver={handleDragOver}
				onDragLeave={handleDragLeave}
				data-testid="knowledge-upload-dropzone"
				style={{
					cursor: "pointer",
					borderRadius: "var(--mantine-radius-md)",
					border: `2px dashed ${isDragging ? "var(--mantine-color-primary-filled)" : "var(--mantine-color-default-border)"}`,
					backgroundColor: isDragging ? "var(--mantine-color-primary-light)" : "transparent",
					transition: "background-color 120ms ease, border-color 120ms ease",
				}}
				p="xl"
			>
				<Stack align="center" gap={6}>
					<IconCloudUpload size={40} stroke={1.4} color="var(--mantine-color-primary-filled)" />
					<Text fw={600}>{t("pages.knowledgeBase.upload.prompt", "Drop documents here or click to browse")}</Text>
					<Text size="sm" c="dimmed" ta="center">
						{t("pages.knowledgeBase.upload.hint", "PDF, DOCX, Markdown, text, CSV, JSON — up to {{limit}} MB each.", {
							limit: KNOWLEDGE_MAX_UPLOAD_SIZE_MB,
						})}
					</Text>
				</Stack>
				<input
					ref={inputRef}
					type="file"
					multiple={true}
					accept={KNOWLEDGE_ACCEPT_ATTRIBUTE}
					onChange={handleInputChange}
					style={{ display: "none" }}
					data-testid="knowledge-upload-input"
				/>
			</Box>

			{pendingUploads.length > 0 ? (
				<Stack gap="xs" data-testid="knowledge-upload-progress">
					{pendingUploads.map((upload) => (
						<Card key={upload.tempId} withBorder={true} radius="sm" p="xs">
							<Stack gap={4}>
								<Group justify="space-between" wrap="nowrap" gap="xs">
									<Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
										<IconFileText size={16} />
										<Text size="sm" truncate="end">
											{upload.name}
										</Text>
									</Group>
									<Text size="xs" c="dimmed" style={{ whiteSpace: "nowrap" }}>
										{formatKnowledgeBytes(upload.sizeBytes)} · {Math.round(upload.percent)}%
									</Text>
								</Group>
								<Progress value={upload.percent} size="sm" radius="xl" animated={true} />
							</Stack>
						</Card>
					))}
				</Stack>
			) : null}
		</Stack>
	);
}
