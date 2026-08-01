import { Alert, Group, Loader, Text } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { RecommendationSnapshot } from "@/features/model-fit/components/RecommendationSnapshot";
import type { ModelFitLatestRecommendations, ModelFitRecommendation } from "@/features/model-fit/models/ModelFitModels";

interface RecommendationsResultsProps {
	readonly isLoading: boolean;
	readonly error: unknown;
	readonly hasCache: boolean;
	readonly latest: ModelFitLatestRecommendations | undefined;
	readonly onDownload: (recommendation: ModelFitRecommendation) => void;
	readonly downloadingModelName: string | null;
}

// The results region of the advisor card: renders exactly one of the loading spinner, the load-error alert, the
// populated snapshot (via RecommendationSnapshot), or the no-cache empty state. The parent owns the use-case selector
// and the surrounding card chrome.
export function RecommendationsResults({ isLoading, error, hasCache, latest, onDownload, downloadingModelName }: RecommendationsResultsProps) {
	const { t } = useTranslation();

	if (isLoading) {
		return (
			<Group gap="sm">
				<Loader size="sm" />
				<Text c="dimmed">{t("pages.modelFit.recommendations.loading", "Loading recommendations…")}</Text>
			</Group>
		);
	}

	if (error) {
		return (
			<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="model-fit-recommendations-error">
				{apiErrorMessage(error, t("pages.modelFit.recommendations.errors.load", "Could not load recommendations."))}
			</Alert>
		);
	}

	if (hasCache && latest) {
		return <RecommendationSnapshot latest={latest} onDownload={onDownload} downloadingModelName={downloadingModelName} />;
	}

	return (
		<Alert color="gray" icon={<IconInfoCircle size={16} />} data-testid="model-fit-no-cache">
			{t(
				"pages.modelFit.recommendations.noCache",
				"No cached recommendation snapshot for this use case yet. Run a recommendation check from the scheduler, or use Refresh now if a schedule exists.",
			)}
		</Alert>
	);
}
