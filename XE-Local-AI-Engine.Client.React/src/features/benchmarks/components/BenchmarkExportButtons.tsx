import { Button, Group } from "@mantine/core";
import { IconFileCode, IconTable } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import type { BenchmarkExportFormat } from "@/features/benchmarks/hooks/useBenchmarkExport";
import { useBenchmarkExport } from "@/features/benchmarks/hooks/useBenchmarkExport";

interface BenchmarkExportButtonsProps {
	projectId: string;
}

/**
 * Downloads one project's whole benchmark record. JSON carries every run at full detail — transcript, launch receipt
 * and judge verdict — so a result can be re-read outside this app; CSV is the same runs as flat rows for a spreadsheet.
 *
 * Both formats are one request each, so a single pending flag covers both buttons.
 */
export function BenchmarkExportButtons({ projectId }: BenchmarkExportButtonsProps) {
	const { t } = useTranslation();
	const exportProject = useBenchmarkExport();
	const download = (format: BenchmarkExportFormat): void => {
		exportProject.mutate(
			{ projectId, format },
			{
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("pages.benchmarks.export.error", "Could not export this benchmark project."))),
			},
		);
	};

	return (
		<Group gap="xs">
			<Button
				variant="default"
				leftSection={<IconFileCode size={16} />}
				loading={exportProject.isPending}
				onClick={() => download("json")}
				data-testid="benchmark-export-json"
			>
				{t("pages.benchmarks.export.json", "Export JSON")}
			</Button>
			<Button
				variant="default"
				leftSection={<IconTable size={16} />}
				loading={exportProject.isPending}
				onClick={() => download("csv")}
				data-testid="benchmark-export-csv"
			>
				{t("pages.benchmarks.export.csv", "Export CSV")}
			</Button>
		</Group>
	);
}
