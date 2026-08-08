// Presentational network log.
//
// Renders a snapshot's network entries (method/url/status/duration/transport/traceId). Bodies are
// never present in the contract (dropped at capture), so this view is redaction-safe.

import { Badge, Table, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { NetworkEntry } from "@/core/diagnostics/Diagnostics";

export interface NetworkLogProps {
	readonly entries: readonly NetworkEntry[];
}

function formatDuration(durationMs: number | undefined): string {
	return durationMs === undefined ? "—" : `${Math.round(durationMs)} ms`;
}

export function NetworkLog({ entries }: NetworkLogProps) {
	const { t } = useTranslation();

	if (entries.length === 0) {
		return (
			<Text c="dimmed" size="sm">
				{t("diagnostics.network.empty")}
			</Text>
		);
	}

	return (
		<Table.ScrollContainer minWidth={640}>
			<Table striped={true} highlightOnHover={true} fz="sm">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("diagnostics.network.method")}</Table.Th>
						<Table.Th>{t("diagnostics.network.url")}</Table.Th>
						<Table.Th>{t("diagnostics.network.status")}</Table.Th>
						<Table.Th>{t("diagnostics.network.duration")}</Table.Th>
						<Table.Th>{t("diagnostics.network.transport")}</Table.Th>
						<Table.Th>{t("diagnostics.network.traceId")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{entries.map((entry) => (
						<Table.Tr key={`${entry.transport}-${entry.method}-${entry.url}-${entry.status ?? ""}-${entry.traceId ?? ""}`}>
							<Table.Td>{entry.method}</Table.Td>
							<Table.Td style={{ wordBreak: "break-all" }}>{entry.url}</Table.Td>
							<Table.Td>{entry.status ?? "—"}</Table.Td>
							<Table.Td>{formatDuration(entry.durationMs)}</Table.Td>
							<Table.Td>
								<Badge variant="light" size="sm">
									{entry.transport}
								</Badge>
							</Table.Td>
							<Table.Td ff="monospace" fz="xs" style={{ wordBreak: "break-all" }}>
								{entry.traceId ?? "—"}
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
