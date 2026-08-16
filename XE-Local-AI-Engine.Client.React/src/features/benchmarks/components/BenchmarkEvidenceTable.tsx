import { Button, CopyButton, ScrollArea, Table, Text } from "@mantine/core";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { BenchmarkEvidenceDiffRow, BenchmarkEvidenceEntry } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import { formatEvidenceValue, isEvidenceHashKey } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";

function EvidenceValue({ entryKey, value, truncate = true }: { entryKey: string; value: unknown; truncate?: boolean }) {
	const { t } = useTranslation();
	const rendered = formatEvidenceValue(entryKey, value, truncate);
	const copyable = isEvidenceHashKey(entryKey) && typeof value === "string" && value.length > 0;
	return (
		<Text component="span" size="xs" title={typeof value === "string" ? value : undefined}>
			{rendered}
			{copyable ? (
				<CopyButton value={value as string}>
					{({ copied, copy }) => (
						<Button
							variant="subtle"
							size="compact-xs"
							ml={4}
							leftSection={copied ? <IconCheck size={12} /> : <IconCopy size={12} />}
							onClick={copy}
						>
							{copied ? t("pages.benchmarks.launch.copied", "Copied") : t("pages.benchmarks.launch.copy", "Copy")}
						</Button>
					)}
				</CopyButton>
			) : null}
		</Text>
	);
}

const fieldCell = (key: string) => (
	<Text component="span" size="xs" c="dimmed" ff="monospace">
		{key}
	</Text>
);

/** One decoded evidence object as a flat field/value table. Field paths are wire identifiers, so they stay unlocalized. */
export function BenchmarkEvidenceTable({
	entries,
	"data-testid": testId,
}: {
	entries: readonly BenchmarkEvidenceEntry[];
	"data-testid"?: string;
}) {
	const { t } = useTranslation();
	return (
		<ScrollArea.Autosize mah={320}>
			<Table striped={true} withTableBorder={true} data-testid={testId}>
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.benchmarks.launch.field", "Field")}</Table.Th>
						<Table.Th>{t("pages.benchmarks.launch.value", "Value")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{entries.map((entry) => (
						<Table.Tr key={entry.key}>
							<Table.Td>{fieldCell(entry.key)}</Table.Td>
							<Table.Td>
								<EvidenceValue entryKey={entry.key} value={entry.value} />
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</ScrollArea.Autosize>
	);
}

/** Both sides of a comparison over the same field set; only the rows whose values disagree are highlighted. */
export function BenchmarkEvidenceDiffTable({
	rows,
	leftLabel,
	rightLabel,
	"data-testid": testId,
}: {
	rows: readonly BenchmarkEvidenceDiffRow[];
	leftLabel: string;
	rightLabel: string;
	"data-testid"?: string;
}) {
	const { t } = useTranslation();
	return (
		<ScrollArea.Autosize mah={320}>
			<Table striped={true} withTableBorder={true} data-testid={testId}>
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.benchmarks.launch.field", "Field")}</Table.Th>
						<Table.Th>{leftLabel}</Table.Th>
						<Table.Th>{rightLabel}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{rows.map((row) => (
						<Table.Tr
							key={row.key}
							data-differs={row.differs ? "true" : undefined}
							bg={row.differs ? "var(--mantine-color-yellow-light)" : undefined}
						>
							<Table.Td>{fieldCell(row.key)}</Table.Td>
							<Table.Td>
								<EvidenceValue entryKey={row.key} value={row.left} truncate={!row.differs} />
							</Table.Td>
							<Table.Td>
								<EvidenceValue entryKey={row.key} value={row.right} truncate={!row.differs} />
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</ScrollArea.Autosize>
	);
}
