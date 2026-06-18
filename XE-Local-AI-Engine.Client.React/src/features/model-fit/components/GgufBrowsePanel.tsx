import { Alert, Anchor, Badge, Button, Card, Group, Loader, Stack, Table, Text, TextInput, Title } from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload, IconExternalLink, IconSearch } from "@tabler/icons-react";
import { type FormEvent, useState } from "react";
import { useTranslation } from "react-i18next";

import { formatModelFitTimestamp } from "@/features/model-fit/components/ModelFitFormatters";
import type { GgufRepository } from "@/features/model-fit/models/ModelFitModels";

interface GgufBrowsePanelProps {
	repositories: readonly GgufRepository[];
	isLoading: boolean;
	error: unknown;
	hasSearched: boolean;
	onSearch: (query: string) => void;
	onDownload: (repository: GgufRepository) => void;
	downloadingRepoId: string | null;
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// HF GGUF browse/select panel: a search box runs browseGgufRepositories, results list each candidate repo with its
// metadata and a select-to-download action (startGgufDownload by the parent). The committed search term is lifted to
// the page (it keys the browse query); the raw input box value is local component state until submitted.
export function GgufBrowsePanel({
	repositories,
	isLoading,
	error,
	hasSearched,
	onSearch,
	onDownload,
	downloadingRepoId,
}: GgufBrowsePanelProps) {
	const { t } = useTranslation();
	const [input, setInput] = useState("");

	const handleSubmit = (event: FormEvent): void => {
		event.preventDefault();
		onSearch(input.trim());
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-browse-card">
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconSearch size={20} />
					<Title order={4}>{t("pages.modelFit.browse.title", "Browse Hugging Face GGUF")}</Title>
				</Group>

				<form onSubmit={handleSubmit}>
					<Group gap="sm" align="flex-end">
						<TextInput
							style={{ flex: 1 }}
							label={t("pages.modelFit.browse.searchLabel", "Search repositories")}
							placeholder={t("pages.modelFit.browse.searchPlaceholder", "e.g. llama 3.1 8b")}
							value={input}
							onChange={(event) => setInput(event.currentTarget.value)}
							data-testid="model-fit-browse-input"
						/>
						<Button
							type="submit"
							leftSection={<IconSearch size={16} />}
							loading={isLoading}
							disabled={input.trim().length === 0}
							data-testid="model-fit-browse-search-button"
						>
							{t("pages.modelFit.browse.search", "Search")}
						</Button>
					</Group>
				</form>

				{error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-browse-error">
						{errorMessage(error, t("pages.modelFit.browse.error", "Could not search repositories."))}
					</Alert>
				) : null}

				{isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.modelFit.browse.loading", "Searching…")}</Text>
					</Group>
				) : null}

				{!isLoading && !error && hasSearched && repositories.length === 0 ? (
					<Text c="dimmed" data-testid="model-fit-browse-empty">
						{t("pages.modelFit.browse.empty", "No GGUF repositories matched that search.")}
					</Text>
				) : null}

				{!isLoading && !error && repositories.length > 0 ? (
					<Table.ScrollContainer minWidth={760}>
						<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="model-fit-browse-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.modelFit.browse.columns.repo", "Repository")}</Table.Th>
									<Table.Th>{t("pages.modelFit.browse.columns.downloads", "Downloads")}</Table.Th>
									<Table.Th>{t("pages.modelFit.browse.columns.likes", "Likes")}</Table.Th>
									<Table.Th>{t("pages.modelFit.browse.columns.updated", "Updated")}</Table.Th>
									<Table.Th>{t("pages.modelFit.browse.columns.license", "License")}</Table.Th>
									<Table.Th>{t("pages.modelFit.browse.columns.action", "Action")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{repositories.map((repository) => (
									<Table.Tr key={repository.repoId} data-testid={`model-fit-browse-row-${repository.repoId}`}>
										<Table.Td>
											<Group gap="xs" wrap="nowrap">
												<Anchor
													href={`https://huggingface.co/${repository.repoId}`}
													target="_blank"
													rel="noopener noreferrer"
													size="sm"
													fw={500}
												>
													{repository.repoId}
													<IconExternalLink size={12} style={{ marginLeft: 4, verticalAlign: "middle" }} />
												</Anchor>
												{repository.isGated ? (
													<Badge color="yellow" variant="light" size="sm">
														{t("pages.modelFit.browse.gated", "Gated")}
													</Badge>
												) : null}
											</Group>
										</Table.Td>
										<Table.Td>{repository.downloads.toLocaleString()}</Table.Td>
										<Table.Td>{repository.likes.toLocaleString()}</Table.Td>
										<Table.Td>{formatModelFitTimestamp(repository.lastModifiedAtUtc)}</Table.Td>
										<Table.Td>{repository.license ?? "—"}</Table.Td>
										<Table.Td>
											<Button
												size="xs"
												variant="light"
												leftSection={<IconCloudDownload size={14} />}
												loading={downloadingRepoId === repository.repoId}
												disabled={!repository.hasUsableGguf || downloadingRepoId === repository.repoId}
												onClick={() => onDownload(repository)}
												data-testid={`model-fit-browse-download-${repository.repoId}`}
											>
												{repository.hasUsableGguf
													? t("pages.modelFit.browse.download", "Download")
													: t("pages.modelFit.browse.noGguf", "No GGUF")}
											</Button>
										</Table.Td>
									</Table.Tr>
								))}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				) : null}
			</Stack>
		</Card>
	);
}
