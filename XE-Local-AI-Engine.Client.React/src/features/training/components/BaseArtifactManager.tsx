import { ActionIcon, Alert, Badge, Button, Group, Progress, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconPlayerStop, IconTrash } from "@tabler/icons-react";
import { type FormEvent, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { downloadPercent, formatBytes, isArtifactDownloading } from "@/features/training/models/TrainingModels";
import {
	useBaseArtifacts,
	useCancelBaseArtifact,
	useCreateBaseArtifact,
	useDeleteBaseArtifact,
} from "@/features/training/queries/useTrainingQueries";

/**
 * Downloads and manages the trainable base checkpoints a run fine-tunes from.
 *
 * The repository is always operator-typed: a model with no resolvable base checkpoint is ineligible for training,
 * and inferring one from an installed GGUF would produce a run against the wrong weights.
 */
export function BaseArtifactManager() {
	const { t } = useTranslation();

	const [repoId, setRepoId] = useState("");
	const [revision, setRevision] = useState("");
	const [submitError, setSubmitError] = useState<string | undefined>(undefined);

	const artifactsQuery = useBaseArtifacts();
	const artifacts = useMemo(() => artifactsQuery.data ?? [], [artifactsQuery.data]);
	const anyDownloading = artifacts.some((artifact) => isArtifactDownloading(artifact.status));

	// Re-subscribes with polling on while a transfer is live, so the progress bar advances.
	const pollingQuery = useBaseArtifacts(anyDownloading);
	const rows = pollingQuery.data ?? artifacts;

	const createMutation = useCreateBaseArtifact();
	const deleteMutation = useDeleteBaseArtifact();
	const cancelMutation = useCancelBaseArtifact();

	const handleSubmit = (event: FormEvent<HTMLFormElement>): void => {
		event.preventDefault();
		setSubmitError(undefined);
		createMutation.mutate(
			{
				body: {
					repoId: repoId.trim(),
					revision: revision.trim().length === 0 ? null : revision.trim(),
				},
			},
			{
				onSuccess: () => {
					setRepoId("");
					setRevision("");
				},
				onError: (error) => {
					setSubmitError(
						error instanceof ApiError && error.message
							? error.message
							: t("pages.training.baseArtifacts.error", "Could not start the download."),
					);
				},
			},
		);
	};

	return (
		<SectionCard title={t("pages.training.baseArtifacts.title", "Base checkpoints")}>
			<Stack gap="md">
				<Text c="dimmed" size="sm">
					{t(
						"pages.training.baseArtifacts.description",
						"Fine-tuning needs the original safetensors checkpoint, not a quantized copy. Enter the Hugging Face repository to download.",
					)}
				</Text>
				<form onSubmit={handleSubmit}>
					<Group align="flex-end" gap="sm">
						<TextInput
							flex={1}
							label={t("pages.training.baseArtifacts.repoId", "Repository")}
							onChange={(event) => setRepoId(event.currentTarget.value)}
							placeholder="unsloth/Llama-3.2-1B-Instruct"
							value={repoId}
						/>
						<TextInput
							label={t("pages.training.baseArtifacts.revision", "Revision (optional)")}
							onChange={(event) => setRevision(event.currentTarget.value)}
							placeholder="main"
							value={revision}
						/>
						<Button disabled={repoId.trim().length === 0} loading={createMutation.isPending} type="submit">
							{t("pages.training.baseArtifacts.download", "Download")}
						</Button>
					</Group>
				</form>

				{submitError == null ? null : (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{submitError}
					</Alert>
				)}

				{rows.length === 0 ? (
					<EmptyState
						message={t(
							"pages.training.baseArtifacts.empty",
							"No base checkpoints yet. Download one to make it available to training runs.",
						)}
					/>
				) : (
					<Table.ScrollContainer minWidth={720}>
						<Table highlightOnHover={true} data-testid="training-base-artifacts-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.training.baseArtifacts.repoId", "Repository")}</Table.Th>
									<Table.Th>{t("pages.training.baseArtifacts.status", "Status")}</Table.Th>
									<Table.Th>{t("pages.training.baseArtifacts.license", "License")}</Table.Th>
									<Table.Th>{t("pages.training.baseArtifacts.size", "Size")}</Table.Th>
									<Table.Th />
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{rows.map((artifact) => {
									const downloading = isArtifactDownloading(artifact.status);
									const percent = downloadPercent(artifact.progress);
									return (
										<Table.Tr key={artifact.id}>
											<Table.Td>
												<Stack gap={2}>
													<Text size="sm">{artifact.repoId}</Text>
													<Text c="dimmed" size="xs">
														{artifact.revision}
													</Text>
												</Stack>
											</Table.Td>
											<Table.Td>
												<Stack gap={4}>
													<Badge
														color={artifact.status === "Ready" ? "green" : artifact.status === "Failed" ? "red" : "blue"}
														variant="light"
													>
														{t(`pages.training.baseArtifacts.statusValue.${artifact.status}`, artifact.status)}
													</Badge>
													{downloading && artifact.progress != null ? (
														<Stack gap={2}>
															{/* An unknown total renders as an indeterminate bar rather than a percentage
															    the transfer would overshoot. */}
															<Progress animated={percent == null} value={percent ?? 100} />
															<Text c="dimmed" size="xs">
																{t("pages.training.baseArtifacts.progress", "File {{index}} of {{count}} — {{done}}", {
																	count: artifact.progress.fileCount,
																	done: formatBytes(artifact.progress.completedBytes),
																	index: artifact.progress.fileIndex,
																})}
															</Text>
														</Stack>
													) : null}
													{artifact.errorMessage == null ? null : (
														<Text c="red" size="xs">
															{artifact.errorMessage}
														</Text>
													)}
												</Stack>
											</Table.Td>
											<Table.Td>
												<Group gap={4}>
													<Text size="sm">
														{artifact.license?.license ?? t("pages.training.baseArtifacts.noLicense", "Not declared")}
													</Text>
													{artifact.license?.isGated === true ? (
														<Badge color="orange" size="xs" variant="light">
															{t("pages.training.baseArtifacts.gated", "Gated")}
														</Badge>
													) : null}
												</Group>
											</Table.Td>
											<Table.Td>
												<Text size="sm">{formatBytes(artifact.totalBytes)}</Text>
											</Table.Td>
											<Table.Td>
												<Group gap={4} justify="flex-end">
													{downloading ? (
														<Tooltip label={t("pages.training.baseArtifacts.cancel", "Cancel download")}>
															<ActionIcon
																aria-label={t("pages.training.baseArtifacts.cancel", "Cancel download")}
																color="orange"
																loading={cancelMutation.isPending}
																onClick={() => cancelMutation.mutate({ path: { artifactId: artifact.id } })}
																variant="subtle"
															>
																<IconPlayerStop size={16} />
															</ActionIcon>
														</Tooltip>
													) : (
														<Tooltip label={t("pages.training.baseArtifacts.delete", "Delete checkpoint")}>
															<ActionIcon
																aria-label={t("pages.training.baseArtifacts.delete", "Delete checkpoint")}
																color="red"
																loading={deleteMutation.isPending}
																onClick={() => deleteMutation.mutate({ path: { artifactId: artifact.id } })}
																variant="subtle"
															>
																<IconTrash size={16} />
															</ActionIcon>
														</Tooltip>
													)}
												</Group>
											</Table.Td>
										</Table.Tr>
									);
								})}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				)}
			</Stack>
		</SectionCard>
	);
}
