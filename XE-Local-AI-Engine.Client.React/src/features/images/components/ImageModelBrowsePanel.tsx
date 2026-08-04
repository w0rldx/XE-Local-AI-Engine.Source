import { Alert, Anchor, Badge, Button, Checkbox, Group, Loader, Select, Stack, Table, Text, TextInput, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconArrowLeft, IconCloudDownload, IconExternalLink, IconSearch } from "@tabler/icons-react";
import { type FormEvent, useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	type ImageModelFamily,
	imageModelFamilies,
	type ImageModelPartRole,
	imageModelPartRoles,
	type ImageRepositoryFileView,
	suggestFamilyForRepo,
} from "@/features/images/models/ImageModels";
import { useBrowseImageRepositories, useInspectImageRepository } from "@/features/images/queries/useImageQueries";
import { formatGgufTimestamp } from "@/features/models/models/GgufFormatters";
import { humanizeBytes } from "@/features/models/models/DownloadRateEstimate";

/** The file-set an operator assembled in this panel, ready for the install mutation. */
export interface BrowseInstallRequest {
	modelName: string;
	repoId: string;
	family: ImageModelFamily;
	parts: readonly {
		role: ImageModelPartRole;
		fileName: string;
		sizeBytes: number;
	}[];
}

interface ImageModelBrowsePanelProps {
	// The model names already installed, so a name collision is caught before the download starts rather than by
	// rediscovering the model already on disk.
	installedModelNames: readonly string[];
	isInstalling: boolean;
	onInstall: (request: BrowseInstallRequest) => void;
}

/** One picked file: which role it fills. Keyed by file name because a repo never lists the same path twice. */
type Selection = Readonly<Record<string, ImageModelPartRole>>;

/**
 * Search → open repository → pick weight files → install. Mirrors the GGUF lane's browse panel, with the one structural
 * difference that matters for diffusion: a GGUF download picks ONE file, while an image model is a file SET, so this
 * panel is a multi-select whose rows each carry a role.
 *
 * The roles are pre-filled from the backend's naming heuristic, which is what turns a four-file FLUX install from four
 * hand-typed file names plus four dropdowns into four ticks.
 */
export function ImageModelBrowsePanel({ installedModelNames, isInstalling, onInstall }: ImageModelBrowsePanelProps) {
	const { t } = useTranslation();
	const [input, setInput] = useState("");
	const [committedQuery, setCommittedQuery] = useState("");
	const [openRepoId, setOpenRepoId] = useState<string | null>(null);
	const [selection, setSelection] = useState<Selection>({});
	const [modelName, setModelName] = useState("");
	const [family, setFamily] = useState<ImageModelFamily>("Sd15");

	const browseQuery = useBrowseImageRepositories(committedQuery);
	const inspectQuery = useInspectImageRepository(openRepoId);

	const repositories = browseQuery.data ?? [];
	const files = useMemo(() => inspectQuery.data?.files ?? [], [inspectQuery.data]);

	const handleSearch = useCallback(
		(event: FormEvent) => {
			event.preventDefault();
			setOpenRepoId(null);
			setSelection({});
			setCommittedQuery(input.trim());
		},
		[input],
	);

	const handleOpenRepo = useCallback((repoId: string) => {
		setOpenRepoId(repoId);
		// A new repo means a new file-set: carrying the previous repo's ticks over would silently install a mix.
		setSelection({});
		// The last path segment is the obvious default install name and is almost always what the operator wants.
		setModelName(repoId.split("/").pop()?.toLowerCase() ?? repoId);
	}, []);

	// Pre-fill the family from the repo + its file names once the inspection lands. Only a starting value: the family
	// drives the generation form's step/CFG defaults, and getting it wrong produces a worse image rather than an error.
	const suggestedFamily = useMemo(
		() => (openRepoId === null ? null : suggestFamilyForRepo(openRepoId, files)),
		[openRepoId, files],
	);
	const [familyTouched, setFamilyTouched] = useState(false);
	const effectiveFamily = familyTouched ? family : (suggestedFamily ?? family);

	const toggleFile = useCallback((file: ImageRepositoryFileView, checked: boolean) => {
		setSelection((current) => {
			if (!checked) {
				const { [file.fileName]: _removed, ...rest } = current;
				return rest;
			}
			return { ...current, [file.fileName]: file.suggestedRole };
		});
	}, []);

	const setRole = useCallback((fileName: string, role: ImageModelPartRole) => {
		setSelection((current) => ({ ...current, [fileName]: role }));
	}, []);

	const selectedFiles = files.filter((file) => selection[file.fileName] !== undefined);
	const hasDiffusion = selectedFiles.some((file) => selection[file.fileName] === "Diffusion");
	// One file per role. The runtime emits one launch flag per role, so a second VAE is passed twice and a second
	// diffusion file is downloaded and never referenced — several gigabytes spent on a model that will not start.
	// The backend rejects this too; catching it here means the operator sees why before paying for the transfer.
	const duplicateRole = selectedFiles.length !== new Set(selectedFiles.map((file) => selection[file.fileName])).size;
	const trimmedName = modelName.trim();
	const isNameTaken = installedModelNames.some((name) => name.toLowerCase() === trimmedName.toLowerCase());
	const canInstall = openRepoId !== null && trimmedName.length > 0 && hasDiffusion && !duplicateRole && !isNameTaken;

	const handleInstall = useCallback(() => {
		if (openRepoId === null || !canInstall) {
			return;
		}
		onInstall({
			modelName: trimmedName,
			repoId: openRepoId,
			family: effectiveFamily,
			// Sizes come straight from the Hub listing, which is what makes the free-disk pre-flight run and the
			// aggregate percentage computable — the two things the hand-typed form could not supply reliably.
			parts: selectedFiles.map((file) => ({
				role: selection[file.fileName] as ImageModelPartRole,
				fileName: file.fileName,
				sizeBytes: file.sizeBytes,
			})),
		});
	}, [canInstall, effectiveFamily, onInstall, openRepoId, selectedFiles, selection, trimmedName]);

	return (
		<Stack gap="md" data-testid="image-model-browse">
			<Text size="sm" c="dimmed">
				{t(
					"pages.images.models.browse.description",
					"Search text-to-image repositories, then pick the weight files that make up the model.",
				)}
			</Text>

			<form onSubmit={handleSearch}>
				<Group gap="sm" align="flex-end">
					<TextInput
						style={{ flex: 1 }}
						label={t("pages.images.models.browse.searchLabel", "Search repositories")}
						placeholder={t("pages.images.models.browse.searchPlaceholder", "e.g. flux schnell")}
						value={input}
						onChange={(event) => setInput(event.currentTarget.value)}
						data-testid="image-model-browse-input"
					/>
					<Button
						type="submit"
						leftSection={<IconSearch size={16} />}
						loading={browseQuery.isFetching}
						disabled={input.trim().length === 0}
						data-testid="image-model-browse-search"
					>
						{t("pages.images.models.browse.search", "Search")}
					</Button>
				</Group>
			</form>

			{browseQuery.error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="image-model-browse-error">
					{apiErrorMessage(browseQuery.error, t("pages.images.models.browse.error", "Could not search repositories."))}
				</Alert>
			) : null}

			{openRepoId === null ? (
				<RepositoryResults
					isSearching={browseQuery.isFetching}
					hasSearched={committedQuery.length > 0}
					repositories={repositories}
					onOpen={handleOpenRepo}
				/>
			) : (
				<Stack gap="sm">
					<Group justify="space-between" wrap="nowrap">
						<Button
							size="xs"
							variant="subtle"
							leftSection={<IconArrowLeft size={14} />}
							onClick={() => setOpenRepoId(null)}
							data-testid="image-model-browse-back"
						>
							{t("pages.images.models.browse.back", "Back to results")}
						</Button>
						<Text size="sm" fw={500} truncate={true}>
							{t("pages.images.models.browse.files.title", "Weight files in {{repoId}}", { repoId: openRepoId })}
						</Text>
					</Group>

					{inspectQuery.isPending ? (
						<Group gap="sm">
							<Loader size="sm" />
							<Text c="dimmed">{t("pages.images.models.browse.files.loading", "Loading files…")}</Text>
						</Group>
					) : inspectQuery.error ? (
						<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="image-model-browse-files-error">
							{apiErrorMessage(inspectQuery.error, t("pages.images.models.browse.files.error", "Could not read this repository's files."))}
						</Alert>
					) : files.length === 0 ? (
						<Text c="dimmed" data-testid="image-model-browse-files-empty">
							{t("pages.images.models.browse.files.empty", "This repository exposes no installable weight files.")}
						</Text>
					) : (
						<>
							<Alert variant="light" color="gray" data-testid="image-model-browse-files-hint">
								{t(
									"pages.images.models.browse.files.hint",
									"Tick each file the model needs and check its role. Sharded files (…-00001-of-00003) are not listed: a part is a single file.",
								)}
							</Alert>
							<Table.ScrollContainer minWidth={620}>
								<Table verticalSpacing="xs" data-testid="image-model-browse-files-table">
									<Table.Thead>
										<Table.Tr>
											<Table.Th>{t("pages.images.models.browse.files.columns.select", "Use")}</Table.Th>
											<Table.Th>{t("pages.images.models.browse.files.columns.file", "File")}</Table.Th>
											<Table.Th>{t("pages.images.models.browse.files.columns.role", "Role")}</Table.Th>
											<Table.Th>{t("pages.images.models.browse.files.columns.size", "Size")}</Table.Th>
										</Table.Tr>
									</Table.Thead>
									<Table.Tbody>
										{files.map((file) => {
											const picked = selection[file.fileName];
											return (
												<Table.Tr key={file.fileName} data-testid={`image-model-browse-file-${file.fileName}`}>
													<Table.Td>
														<Checkbox
															checked={picked !== undefined}
															aria-label={file.fileName}
															onChange={(event) => toggleFile(file, event.currentTarget.checked)}
															data-testid={`image-model-browse-file-check-${file.fileName}`}
														/>
													</Table.Td>
													<Table.Td>
														<Text size="xs" style={{ wordBreak: "break-all" }}>
															{file.fileName}
														</Text>
													</Table.Td>
													<Table.Td>
														<Select
															size="xs"
															w={190}
															disabled={picked === undefined}
															data={imageModelPartRoles.map((role) => ({
																value: role,
																label: t(`pages.images.models.partRoles.${role}`, role),
															}))}
															value={picked ?? file.suggestedRole}
															allowDeselect={false}
															onChange={(value) => setRole(file.fileName, (value ?? file.suggestedRole) as ImageModelPartRole)}
															data-testid={`image-model-browse-file-role-${file.fileName}`}
														/>
													</Table.Td>
													<Table.Td>
														<Text size="xs" c="dimmed">
															{humanizeBytes(file.sizeBytes)}
														</Text>
													</Table.Td>
												</Table.Tr>
											);
										})}
									</Table.Tbody>
								</Table>
							</Table.ScrollContainer>

							<Group grow={true} align="flex-start">
								<TextInput
									label={t("pages.images.models.browse.modelName.label", "Install as")}
									placeholder={t("pages.images.models.browse.modelName.placeholder", "flux.1-schnell")}
									value={modelName}
									error={isNameTaken ? t("pages.images.models.browse.nameTaken", "A model with that name is already installed.") : undefined}
									onChange={(event) => setModelName(event.currentTarget.value)}
									data-testid="image-model-browse-name"
								/>
								<Select
									label={t("pages.images.models.browse.family.label", "Family")}
									data={imageModelFamilies.map((value) => ({
										value,
										label: t(`pages.images.models.families.${value}`, value),
									}))}
									value={effectiveFamily}
									allowDeselect={false}
									onChange={(value) => {
										setFamilyTouched(true);
										setFamily((current) => (value ?? current) as ImageModelFamily);
									}}
									data-testid="image-model-browse-family"
								/>
							</Group>

							<Group justify="space-between">
								{!hasDiffusion ? (
									<Text size="xs" c="red" data-testid="image-model-browse-diffusion-required">
										{t("pages.images.models.browse.diffusionRequired", "Select one diffusion file before installing.")}
									</Text>
								) : duplicateRole ? (
									<Text size="xs" c="red" data-testid="image-model-browse-duplicate-role">
										{t("pages.images.models.browse.duplicateRole", "Each role can only be filled by one file.")}
									</Text>
								) : (
									<span />
								)}
								<Button
									leftSection={<IconCloudDownload size={16} />}
									loading={isInstalling}
									disabled={!canInstall || isInstalling}
									onClick={handleInstall}
									data-testid="image-model-browse-install"
								>
									{t("pages.images.models.browse.install", "Install selected files")}
								</Button>
							</Group>
						</>
					)}
				</Stack>
			)}
		</Stack>
	);
}

interface RepositoryResultsProps {
	isSearching: boolean;
	hasSearched: boolean;
	repositories: readonly {
		repoId: string;
		isGated: boolean;
		downloads: number;
		likes: number;
		lastModifiedAtUtc: number;
		license: string | null;
		hasUsableWeights: boolean;
		isTrustedPublisher: boolean;
	}[];
	onOpen: (repoId: string) => void;
}

// The result table. Gating and publisher trust are badged rather than filtered — a gated repo is still browsable, it
// just cannot be installed without a token, and a one-click install that 401s on a first run is a bad first run.
function RepositoryResults({ isSearching, hasSearched, repositories, onOpen }: RepositoryResultsProps) {
	const { t } = useTranslation();

	if (isSearching) {
		return (
			<Group gap="sm">
				<Loader size="sm" />
				<Text c="dimmed">{t("pages.images.models.browse.searching", "Searching…")}</Text>
			</Group>
		);
	}

	if (hasSearched && repositories.length === 0) {
		return (
			<Text c="dimmed" data-testid="image-model-browse-empty">
				{t("pages.images.models.browse.empty", "No image repositories matched that search.")}
			</Text>
		);
	}

	if (repositories.length === 0) {
		return null;
	}

	return (
		<Table.ScrollContainer minWidth={720}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="image-model-browse-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.images.models.browse.columns.repo", "Repository")}</Table.Th>
						<Table.Th>{t("pages.images.models.browse.columns.downloads", "Downloads")}</Table.Th>
						<Table.Th>{t("pages.images.models.browse.columns.likes", "Likes")}</Table.Th>
						<Table.Th>{t("pages.images.models.browse.columns.updated", "Updated")}</Table.Th>
						<Table.Th>{t("pages.images.models.browse.columns.license", "License")}</Table.Th>
						<Table.Th>{t("pages.images.models.browse.columns.action", "Action")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{repositories.map((repository) => (
						<Table.Tr key={repository.repoId} data-testid={`image-model-browse-row-${repository.repoId}`}>
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
										<Tooltip
											label={t(
												"pages.images.models.browse.gatedHint",
												"This repository requires an accepted licence and a Hugging Face token — a one-click install will fail with 401 until you add one.",
											)}
											multiline={true}
											maw={280}
										>
											<Badge color="yellow" variant="light" size="sm" data-testid={`image-model-browse-gated-${repository.repoId}`}>
												{t("pages.images.models.browse.gated", "Gated")}
											</Badge>
										</Tooltip>
									) : null}
									{repository.isTrustedPublisher ? null : (
										<Tooltip
											label={t(
												"pages.images.models.browse.untrustedPublisherHint",
												"This publisher is not a known packager — review the repository before downloading.",
											)}
											multiline={true}
											maw={280}
										>
											<Badge
												color="orange"
												variant="light"
												size="sm"
												leftSection={<IconAlertTriangle size={12} />}
												data-testid={`image-model-browse-untrusted-${repository.repoId}`}
											>
												{t("pages.images.models.browse.untrustedPublisher", "Unverified publisher")}
											</Badge>
										</Tooltip>
									)}
								</Group>
							</Table.Td>
							<Table.Td>{repository.downloads.toLocaleString()}</Table.Td>
							<Table.Td>{repository.likes.toLocaleString()}</Table.Td>
							<Table.Td>{formatGgufTimestamp(repository.lastModifiedAtUtc)}</Table.Td>
							<Table.Td>{repository.license ?? "—"}</Table.Td>
							<Table.Td>
								<Button
									size="xs"
									variant="light"
									disabled={!repository.hasUsableWeights}
									onClick={() => onOpen(repository.repoId)}
									data-testid={`image-model-browse-open-${repository.repoId}`}
								>
									{repository.hasUsableWeights
										? t("pages.images.models.browse.select", "Open")
										: t("pages.images.models.browse.noWeights", "No weights")}
								</Button>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
