import { Button, Group, Tooltip } from "@mantine/core";
import { IconFileCode, IconTable } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import type { BenchmarkExportFormat } from "@/features/benchmarks/hooks/useBenchmarkExport";
import { useBenchmarkExport } from "@/features/benchmarks/hooks/useBenchmarkExport";

interface BenchmarkExportButtonsProps {
	projectId: string;
}

/** What the node currently writes. Named on the buttons so a downloaded file's shape is known before it is opened. */
const benchmarkExportSchemaVersion = 6;

/**
 * Downloads one project's whole benchmark record. JSON carries every run at full detail — transcript, launch receipt
 * and judge verdict — so a result can be re-read outside this app; CSV is the same runs as flat rows for a spreadsheet.
 *
 * Schema 6 adds each cell's agent definition, name and version, so two agent variants of one model read as two cells,
 * on top of schema 5's review decisions, verifier artifact metadata (never the bytes) and tool-call rollup and schema 4's
 * task items, per-run item and cell stamps and combinations section. The version
 * is on the buttons because a consumer of an exported file has to know which shape it got, and the file itself is
 * usually read by something other than a person.
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
			<Tooltip
				label={t(
					"pages.benchmarks.export.schemaHelp",
					"Schema {{version}}: every run at full detail with its review and tool-call rollup, plus the project's task items, each run's item and combination stamps, and the combination table.",
					{ version: benchmarkExportSchemaVersion },
				)}
				multiline={true}
				w={320}
			>
				<Button
					variant="default"
					leftSection={<IconFileCode size={16} />}
					loading={exportProject.isPending}
					onClick={() => download("json")}
					data-testid="benchmark-export-json"
				>
					{t("pages.benchmarks.export.json", "Export JSON (v{{version}})", { version: benchmarkExportSchemaVersion })}
				</Button>
			</Tooltip>
			<Button
				variant="default"
				leftSection={<IconTable size={16} />}
				loading={exportProject.isPending}
				onClick={() => download("csv")}
				data-testid="benchmark-export-csv"
			>
				{t("pages.benchmarks.export.csv", "Export CSV (v{{version}})", { version: benchmarkExportSchemaVersion })}
			</Button>
		</Group>
	);
}
