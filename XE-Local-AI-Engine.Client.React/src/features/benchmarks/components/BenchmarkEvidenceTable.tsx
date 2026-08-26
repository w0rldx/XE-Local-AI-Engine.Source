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

/**
 * Every compared side over the same field set, one column each; only the rows whose values disagree are highlighted.
 * Column N is side N by construction — {@link diffLaunchEvidence} pads a missing field with null rather than a hole,
 * so a run that never recorded a field still occupies its own column.
 *
 * A differing row renders its values UNTRUNCATED: two different hashes sharing a 12-character prefix would otherwise
 * print as the same string, which reads as a highlighted row whose values are identical.
 */
export function BenchmarkEvidenceDiffTable({
	rows,
	labels,
	"data-testid": testId,
}: {
	rows: readonly BenchmarkEvidenceDiffRow[];
	labels: readonly string[];
	"data-testid"?: string;
}) {
	const { t } = useTranslation();
	return (
		<ScrollArea.Autosize mah={320}>
			<Table striped={true} withTableBorder={true} data-testid={testId}>
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.benchmarks.launch.field", "Field")}</Table.Th>
						{labels.map((label, index) => (
							// The label is the run's own name, which repeats across a model's quants; the index is what keeps
							// two columns of one model apart as React keys.
							// biome-ignore lint/suspicious/noArrayIndexKey: a column IS its position in the compared set.
							<Table.Th key={`${label}-${index}`}>{label}</Table.Th>
						))}
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
							{labels.map((label, index) => (
								// biome-ignore lint/suspicious/noArrayIndexKey: a column IS its position in the compared set.
								<Table.Td key={`${label}-${index}`}>
									<EvidenceValue entryKey={row.key} value={row.values[index] ?? null} truncate={!row.differs} />
								</Table.Td>
							))}
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</ScrollArea.Autosize>
	);
}
