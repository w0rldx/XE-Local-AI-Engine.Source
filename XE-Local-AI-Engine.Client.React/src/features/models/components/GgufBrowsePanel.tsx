import {
	Anchor,
	Badge,
	Button,
	Card,
	CloseButton,
	Group,
	Loader,
	Stack,
	Table,
	Text,
	TextInput,
	Title,
	Tooltip,
} from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload, IconExternalLink, IconSearch } from "@tabler/icons-react";
import { type FormEvent, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatBytesAsGb } from "@/core/formatting/BytesFormatting";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import {
	fitVerdictColor,
	fitVerdictLabelKey,
	type GgufDownloadTarget,
	type GgufRepository,
	type GgufTestedModel,
} from "@/features/models/models/GgufModels";

interface GgufBrowsePanelProps {
	repositories: readonly GgufRepository[];
	isLoading: boolean;
	error: unknown;
	hasSearched: boolean;
	onSearch: (query: string) => void;
	onDownload: (target: GgufDownloadTarget) => void;
	downloadingRepoId: string | null;
	// Curated-catalog models the authors tested, offered before any search; empty hides the block.
	testedModels: readonly GgufTestedModel[];
	// Lower-cased repo ids with at least one installed quant (installed GGUF models are named `{repo}:{quant}`).
	installedRepoIds: ReadonlySet<string>;
}

// HF GGUF browse/select panel: a search box runs browseGgufRepositories, results list each candidate repo with its
// metadata and a select-to-download action (startGgufDownload by the parent). The committed search term is lifted to
// the page (it keys the browse query); the raw input box value is local component state until submitted. Until a
// search is submitted, or after the field is cleared, the tested catalog models are listed so a new user has something
// to pick without searching.
export function GgufBrowsePanel({
	repositories,
	isLoading,
	error,
	hasSearched,
	onSearch,
	onDownload,
	downloadingRepoId,
	testedModels,
	installedRepoIds,
}: GgufBrowsePanelProps) {
	const { t } = useTranslation();
	const [input, setInput] = useState("");

	const handleSubmit = (event: FormEvent): void => {
		event.preventDefault();
		onSearch(input.trim());
	};

	const handleClear = (): void => {
		setInput("");
		onSearch("");
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="model-fit-browse-card">
			<Stack gap="md">
				<Group gap="xs" align="center">
					<IconSearch size={20} />
					<Title order={2} size="h4">
						{t("pages.models.gguf.browse.title", "Browse Hugging Face GGUF")}
					</Title>
				</Group>

				<form onSubmit={handleSubmit}>
					<Group gap="sm" align="flex-end">
						<TextInput
							style={{ flex: 1 }}
							label={t("pages.models.gguf.browse.searchLabel", "Search repositories")}
							placeholder={t("pages.models.gguf.browse.searchPlaceholder", "e.g. llama 3.1 8b")}
							value={input}
							onChange={(event) => setInput(event.currentTarget.value)}
							rightSection={
								input.length > 0 || hasSearched ? (
									<CloseButton
										size="sm"
										onClick={handleClear}
										aria-label={t("pages.models.gguf.browse.clearSearch", "Clear search")}
										data-testid="model-fit-browse-clear"
									/>
								) : null
							}
							data-testid="model-fit-browse-input"
						/>
						<Button
							type="submit"
							leftSection={<IconSearch size={16} />}
							loading={isLoading}
							disabled={input.trim().length === 0}
							data-testid="model-fit-browse-search-button"
						>
							{t("pages.models.gguf.browse.search", "Search")}
						</Button>
					</Group>
				</form>

				{!hasSearched && testedModels.length > 0 ? (
					<Stack gap="xs">
						<Title order={3} size="h5">
							{t("pages.models.gguf.browse.tested.title", "Tested by the authors")}
						</Title>
						<Text size="sm" c="dimmed">
							{t(
								"pages.models.gguf.browse.tested.hint",
								"These models passed the authors' live scenario checks. Pick one to choose a quantization that fits this machine, or search Hugging Face above.",
							)}
						</Text>
						<Table.ScrollContainer minWidth={760}>
							<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="model-fit-browse-tested-table">
								<Table.Thead>
									<Table.Tr>
										<Table.Th>{t("pages.models.gguf.browse.tested.columns.model", "Model")}</Table.Th>
										<Table.Th>{t("pages.models.gguf.browse.tested.columns.size", "Size")}</Table.Th>
										<Table.Th>{t("pages.models.gguf.browse.tested.columns.testedQuant", "Tested quant")}</Table.Th>
										<Table.Th>{t("pages.models.gguf.browse.columns.license", "License")}</Table.Th>
										<Table.Th>{t("pages.models.gguf.browse.tested.columns.notes", "Notes")}</Table.Th>
										<Table.Th>{t("pages.models.gguf.browse.columns.action", "Action")}</Table.Th>
									</Table.Tr>
								</Table.Thead>
								<Table.Tbody>
									{testedModels.map((model) => {
										const fitColor = fitVerdictColor[model.fitVerdict];
										const fitLabelKey = model.fitVerdict === "Unknown" ? null : fitVerdictLabelKey[model.fitVerdict];
										const isInstalled = installedRepoIds.has(model.ggufRepo.toLowerCase());
										return (
											<Table.Tr key={model.id} data-testid={`model-fit-browse-tested-row-${model.id}`}>
												<Table.Td>
													<Group gap="xs" wrap="nowrap">
														<Text size="sm" fw={500}>
															{model.displayName}
														</Text>
														{isInstalled ? (
															<Badge
																color="teal"
																variant="light"
																size="sm"
																data-testid={`model-fit-browse-tested-installed-${model.id}`}
															>
																{t("pages.models.gguf.browse.tested.installed", "Installed")}
															</Badge>
														) : null}
													</Group>
													<Anchor
														href={`https://huggingface.co/${model.ggufRepo}`}
														target="_blank"
														rel="noopener noreferrer"
														size="xs"
													>
														{model.ggufRepo}
														<IconExternalLink size={12} style={{ marginLeft: 4, verticalAlign: "middle" }} />
													</Anchor>
												</Table.Td>
												<Table.Td>{`${model.totalParamsB}B`}</Table.Td>
												<Table.Td>
													<Group gap="xs" wrap="nowrap">
														<Text size="sm" data-testid={`model-fit-browse-tested-quant-${model.id}`}>
															{`${model.testedQuant} · ${formatBytesAsGb(model.testedSizeBytes)}`}
														</Text>
														{fitColor !== null && fitLabelKey !== null ? (
															<Tooltip
																label={
																	model.fitVerdict === "WontFit"
																		? t(
																				"pages.models.gguf.browse.tested.fitHintWontFit",
																				"The tested quant does not fit this machine. Download opens the picker, which offers smaller quants.",
																			)
																		: t(
																				"pages.models.gguf.browse.tested.fitHint",
																				"Compares the tested file's size with this machine's free memory. The Model Recommendations page does the detailed sizing, including context.",
																			)
																}
																multiline={true}
																maw={260}
															>
																<Badge
																	color={fitColor}
																	variant="light"
																	size="sm"
																	data-testid={`model-fit-browse-tested-fit-${model.id}`}
																>
																	{t(`pages.models.gguf.download.fit.${fitLabelKey}`, model.fitVerdict)}
																</Badge>
															</Tooltip>
														) : null}
													</Group>
												</Table.Td>
												<Table.Td>{model.license}</Table.Td>
												<Table.Td>{model.notes ?? "—"}</Table.Td>
												<Table.Td>
													<Button
														size="xs"
														variant="light"
														leftSection={<IconCloudDownload size={14} />}
														loading={downloadingRepoId === model.ggufRepo}
														disabled={downloadingRepoId === model.ggufRepo}
														onClick={() => onDownload({ repoId: model.ggufRepo, preferredQuant: model.testedQuant })}
														data-testid={`model-fit-browse-tested-download-${model.id}`}
													>
														{/* Installed means some quant of the repo is; the picker can still fetch another one. */}
														{isInstalled
															? t("pages.models.gguf.browse.downloadOtherQuant", "Other quant")
															: t("pages.models.gguf.browse.download", "Download")}
													</Button>
												</Table.Td>
											</Table.Tr>
										);
									})}
								</Table.Tbody>
							</Table>
						</Table.ScrollContainer>
					</Stack>
				) : null}

				{error ? (
					<InlineErrorAlert
						message={apiErrorMessage(error, t("pages.models.gguf.browse.error", "Could not search repositories."))}
						data-testid="model-fit-browse-error"
					/>
				) : null}

				{isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.models.gguf.browse.loading", "Searching…")}</Text>
					</Group>
				) : null}

				{!isLoading && !error && hasSearched && repositories.length === 0 ? (
					<Text c="dimmed" data-testid="model-fit-browse-empty">
						{t("pages.models.gguf.browse.empty", "No GGUF repositories matched that search.")}
					</Text>
				) : null}

				{!isLoading && !error && hasSearched && repositories.length > 0 ? (
					<Table.ScrollContainer minWidth={760}>
						<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="model-fit-browse-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.models.gguf.browse.columns.repo", "Repository")}</Table.Th>
									<Table.Th>{t("pages.models.gguf.browse.columns.downloads", "Downloads")}</Table.Th>
									<Table.Th>{t("pages.models.gguf.browse.columns.likes", "Likes")}</Table.Th>
									<Table.Th>{t("pages.models.gguf.browse.columns.updated", "Updated")}</Table.Th>
									<Table.Th>{t("pages.models.gguf.browse.columns.license", "License")}</Table.Th>
									<Table.Th>{t("pages.models.gguf.browse.columns.action", "Action")}</Table.Th>
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
														{t("pages.models.gguf.browse.gated", "Gated")}
													</Badge>
												) : null}
												{!repository.isTrustedPublisher ? (
													<Tooltip
														label={t(
															"pages.models.gguf.browse.untrustedPublisherHint",
															"This publisher is not a known GGUF packager — review the repo before downloading.",
														)}
														multiline={true}
														maw={260}
													>
														<Badge
															color="orange"
															variant="light"
															size="sm"
															leftSection={<IconAlertTriangle size={12} />}
															data-testid={`model-fit-browse-untrusted-${repository.repoId}`}
														>
															{t("pages.models.gguf.browse.untrustedPublisher", "Unverified publisher")}
														</Badge>
													</Tooltip>
												) : null}
											</Group>
										</Table.Td>
										<Table.Td>{repository.downloads.toLocaleString()}</Table.Td>
										<Table.Td>{repository.likes.toLocaleString()}</Table.Td>
										<Table.Td>{formatTimestamp(repository.lastModifiedAtUtc)}</Table.Td>
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
													? t("pages.models.gguf.browse.download", "Download")
													: t("pages.models.gguf.browse.noGguf", "No GGUF")}
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
