import { Button, Group, Loader, Select, Stack, Text } from "@mantine/core";
import { IconGitBranch } from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import type { DevelopmentRepository } from "@/features/development/models/DevelopmentModels";

interface KnowledgeRepositoryImportPanelProps {
	readonly repositories: readonly DevelopmentRepository[];
	readonly isLoading: boolean;
	readonly isImporting: boolean;
	onImport(selectedFolderId: string): void;
}

/** Bounded repository-import selector; the server resolves the opaque folder id and enforces Git/safety boundaries. */
export function KnowledgeRepositoryImportPanel({
	repositories,
	isLoading,
	isImporting,
	onImport,
}: KnowledgeRepositoryImportPanelProps) {
	const { t } = useTranslation();
	const [selectedFolderId, setSelectedFolderId] = useState<string | null>(null);
	const choices = useMemo(
		() =>
			repositories.map((repository) => ({
				value: repository.id,
				label: repository.alias,
				disabled: repository.availability !== "Available",
			})),
		[repositories],
	);

	return (
		<Stack gap="sm">
			<Text size="sm" c="dimmed">
				{t(
					"pages.knowledgeBase.repository.hint",
					"Index tracked and unignored text/code files from a registered Git repository into the active collection.",
				)}
			</Text>
			<Group align="flex-end" wrap="wrap">
				<Select
					label={t("pages.knowledgeBase.repository.label", "Registered repository")}
					placeholder={t("pages.knowledgeBase.repository.placeholder", "Choose a repository")}
					data={choices}
					value={selectedFolderId}
					onChange={setSelectedFolderId}
					searchable={true}
					nothingFoundMessage={t("pages.knowledgeBase.repository.empty", "No available repositories")}
					leftSection={isLoading ? <Loader size={14} /> : <IconGitBranch size={16} />}
					disabled={isLoading || isImporting}
					style={{ flex: "1 1 18rem" }}
					data-testid="knowledge-repository-select"
				/>
				<Button
					onClick={() => selectedFolderId && onImport(selectedFolderId)}
					disabled={!selectedFolderId || isLoading || isImporting}
					loading={isImporting}
					data-testid="knowledge-repository-import"
				>
					{t("pages.knowledgeBase.repository.action", "Import repository")}
				</Button>
			</Group>
		</Stack>
	);
}
