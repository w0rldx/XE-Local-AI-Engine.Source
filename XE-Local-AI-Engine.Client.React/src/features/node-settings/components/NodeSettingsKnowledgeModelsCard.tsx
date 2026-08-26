import { Button, Card, Group, Select, Stack, Text, Title } from "@mantine/core";
import { IconCloudDownload, IconDatabase } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsFieldError,
	nodeSettingsRestartHint,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import type { NodeSettingsModelOption } from "@/features/node-settings/components/NodeSettingsRuntimeCard";
import type { NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	readonly rerankerModelOptions: readonly NodeSettingsModelOption[];
	readonly rerankerDownload: { readonly onStart: () => void; readonly pending: boolean; readonly inFlight: boolean };
	readonly embeddingDownload: { readonly onStart: () => void; readonly pending: boolean; readonly inFlight: boolean };
}
export function NodeSettingsKnowledgeModelsCard({
	form,
	errors,
	onChange,
	rerankerModelOptions,
	rerankerDownload,
	embeddingDownload,
}: Props) {
	const { t } = useTranslation();
	const rerankerOptions = useMemo(() => {
		const options = [{ value: "", label: t("pages.nodeSettings.fields.rerankerModel.off", "Off") }, ...rerankerModelOptions];
		if (form.rerankerModelName !== "" && !options.some((option) => option.value === form.rerankerModelName)) {
			options.push({ value: form.rerankerModelName, label: form.rerankerModelName });
		}
		return options;
	}, [rerankerModelOptions, form.rerankerModelName, t]);
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-knowledge-models-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={4}>{t("pages.nodeSettings.fields.knowledgeModels.title", "Knowledge models")}</Title>
					<IconDatabase size={20} />
				</Group>
				<Group justify="space-between" align="center" wrap="nowrap" gap="md">
					<Text size="xs" c="dimmed">
						{t(
							"pages.nodeSettings.fields.embeddingModel.recommendedHelp",
							"Required for the knowledge base: without an embedding model, documents cannot be indexed. Recommended: nomic-embed-text-v1.5 (~274 MB).",
						)}
					</Text>
					<Button
						variant="light"
						size="xs"
						leftSection={<IconCloudDownload size={14} />}
						onClick={embeddingDownload.onStart}
						loading={embeddingDownload.pending}
						disabled={embeddingDownload.pending || embeddingDownload.inFlight}
						data-testid="node-settings-embedding-download-recommended"
					>
						{t("pages.nodeSettings.fields.embeddingModel.downloadRecommended", "Download recommended embedding model")}
					</Button>
				</Group>
				<Select
					label={t("pages.nodeSettings.fields.rerankerModel.label", "Reranker model")}
					description={
						<>
							{t(
								"pages.nodeSettings.fields.rerankerModel.description",
								"Cross-encoder reranker that reorders knowledge-base search results for relevance. Leave off if no reranker model is installed. Uses additional VRAM not counted by capacity checks.",
							)}
							{nodeSettingsRestartHint(t, "rerankerModelName")}
						</>
					}
					data={rerankerOptions}
					value={form.rerankerModelName}
					onChange={(value) => onChange("rerankerModelName", value ?? "")}
					allowDeselect={false}
					searchable={true}
					nothingFoundMessage={t("pages.nodeSettings.fields.rerankerModel.empty", "No installed models")}
					error={nodeSettingsFieldError(t, errors, "rerankerModelName")}
					data-testid="node-settings-reranker-model"
				/>
				<Group justify="space-between" align="center" wrap="nowrap" gap="md">
					<Text size="xs" c="dimmed">
						{t(
							"pages.nodeSettings.fields.rerankerModel.recommendedHelp",
							"Recommended: bge-reranker-v2-m3, which runs as its own extra model server.",
						)}
					</Text>
					<Button
						variant="light"
						size="xs"
						leftSection={<IconCloudDownload size={14} />}
						onClick={rerankerDownload.onStart}
						loading={rerankerDownload.pending}
						disabled={rerankerDownload.pending || rerankerDownload.inFlight}
						data-testid="node-settings-reranker-download-recommended"
					>
						{t("pages.nodeSettings.fields.rerankerModel.downloadRecommended", "Download recommended reranker")}
					</Button>
				</Group>
			</Stack>
		</Card>
	);
}
