import { Badge, Table } from "@mantine/core";
import { Fragment } from "react";
import { useTranslation } from "react-i18next";

import { AttemptTerminalReason } from "@/features/development/components/DevelopmentStatusPresentation";
import type { DevelopmentAttempt } from "@/features/development/models/DevelopmentModels";
import { statusColor } from "@/features/development/models/DevelopmentStatusModel";

export function DevelopmentAttemptsTable({ attempts }: { readonly attempts: readonly DevelopmentAttempt[] }) {
	const { t } = useTranslation();
	return (
		<Table.ScrollContainer minWidth={700}>
			<Table highlightOnHover={true} striped={true}>
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.development.attempts.role", "Role")}</Table.Th>
						<Table.Th>{t("pages.development.attempts.model", "Model")}</Table.Th>
						<Table.Th>{t("pages.development.attempts.provider", "Provider")}</Table.Th>
						<Table.Th>{t("pages.development.attempts.status", "Status")}</Table.Th>
						<Table.Th>{t("pages.development.attempts.tokens", "Tokens")}</Table.Th>
						<Table.Th>{t("pages.development.attempts.predecessor", "Predecessor")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{attempts.map((attempt) => (
						<Fragment key={attempt.id}>
							<Table.Tr data-testid={`development-attempt-${attempt.id}`}>
								<Table.Td>{attempt.role}</Table.Td>
								<Table.Td>{attempt.modelId}</Table.Td>
								<Table.Td>{attempt.provider}</Table.Td>
								<Table.Td>
									<Badge color={statusColor(attempt.status)}>{attempt.status}</Badge>
								</Table.Td>
								<Table.Td>{(attempt.inputTokens ?? 0) + (attempt.outputTokens ?? 0)}</Table.Td>
								<Table.Td>{attempt.predecessorAttemptId?.slice(0, 8) ?? "—"}</Table.Td>
							</Table.Tr>
							{attempt.terminalReason ? (
								<Table.Tr data-testid={`development-attempt-reason-${attempt.id}`}>
									<Table.Td colSpan={6} py="xs">
										<AttemptTerminalReason color={statusColor(attempt.status)} reason={attempt.terminalReason} />
									</Table.Td>
								</Table.Tr>
							) : null}
						</Fragment>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
