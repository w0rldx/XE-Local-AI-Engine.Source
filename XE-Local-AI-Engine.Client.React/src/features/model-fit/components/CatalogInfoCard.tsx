import { Badge, Button, Card, Group, Text } from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { ModelFitCatalogInfo } from "@/features/model-fit/models/ModelFitModels";

interface CatalogInfoCardProps {
	readonly catalog: ModelFitCatalogInfo;
	readonly onRefresh: () => void;
	readonly isRefreshing: boolean;
}

// Read-only footer card for the curated model catalog the advisor ranks against: version, source, and freshness, plus a
// Refresh-catalog action. The parent renders it only once catalog data has loaded.
export function CatalogInfoCard({ catalog, onRefresh, isRefreshing }: CatalogInfoCardProps) {
	const { t } = useTranslation();

	return (
		<Card withBorder={true} radius="md" p="md" data-testid="model-fit-catalog-info">
			<Group justify="space-between" align="center" wrap="wrap">
				<Group gap="md" wrap="wrap">
					<Text size="sm" c="dimmed" data-testid="model-fit-catalog-version">
						{t("pages.modelFit.recommendations.catalog.version", "Catalog v{{version}}", {
							version: catalog.catalogVersion,
						})}
					</Text>
					<Badge variant="light" data-testid="model-fit-catalog-source">
						{t(`pages.modelFit.recommendations.catalog.source.${catalog.source}`, catalog.source)}
					</Badge>
					{catalog.updatedAt ? (
						<Text size="sm" c="dimmed" data-testid="model-fit-catalog-updated-at">
							{t("pages.modelFit.recommendations.catalog.updatedAt", "Updated {{time}}", {
								time: new Date(catalog.updatedAt).toLocaleString(),
							})}
						</Text>
					) : null}
				</Group>
				<Button
					variant="default"
					size="xs"
					leftSection={<IconRefresh size={14} />}
					loading={isRefreshing}
					disabled={isRefreshing}
					onClick={onRefresh}
					data-testid="model-fit-catalog-refresh-button"
				>
					{t("pages.modelFit.recommendations.catalog.refreshButton", "Refresh catalog")}
				</Button>
			</Group>
		</Card>
	);
}
