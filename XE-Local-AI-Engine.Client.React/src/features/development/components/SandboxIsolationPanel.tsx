import { Badge, Stack, Table, Text } from "@mantine/core";
import { IconShieldLock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsDevelopmentV1SandboxIsolationSummaryResponse as SandboxIsolation } from "@/core/api/generated/types.gen";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";

interface SandboxIsolationPanelProps {
	/**
	 * One entry per sandbox role. Undefined while the capability query is in flight, and empty from a backend that
	 * predates this field — both render nothing rather than an empty table that reads as "no isolation".
	 */
	readonly roles: readonly SandboxIsolation[] | undefined;
}

/**
 * What each sandbox role on this node is actually isolated by.
 *
 * Reported per role because provider selection is per feature: AgentHome (and with it `run_python`), Development Mode
 * and work sessions each resolve their own provider, and a single node-wide claim would be wrong about at least one of
 * them on a mixed node. The level is derived on the backend from the provider's advertised capabilities — the same
 * flags the fail-closed launch policy gates on — so nothing shown here can claim a boundary the launch path would not
 * enforce.
 *
 * Every value carries a test id, for the same reason the container-runtime panel's do: a test that only asserted the
 * table rendered would pass against a table full of the wrong answers, on the one surface where that matters most.
 */
export function SandboxIsolationPanel({ roles }: SandboxIsolationPanelProps) {
	const { t } = useTranslation();

	if (roles === undefined || roles.length === 0) {
		return null;
	}

	const reasons = roles.filter((role) => (role.filesystemIsolationUnavailableReason ?? "") !== "");

	return (
		<SectionCard
			title={t("pages.development.isolation.title", "Sandbox isolation")}
			icon={<IconShieldLock size={22} />}
			data-testid="sandbox-isolation"
		>
			<Text size="sm" c="dimmed">
				{t(
					"pages.development.isolation.intro",
					"Measured on this host, per sandbox role. A role only claims a boundary its provider advertises and the launch path enforces.",
				)}
			</Text>

			<Table.ScrollContainer minWidth={640}>
				<Table striped={true} highlightOnHover={true} withTableBorder={true}>
					<Table.Caption>
						{t("pages.development.isolation.caption", "Isolation posture of each sandbox role on this node.")}
					</Table.Caption>
					<Table.Thead>
						<Table.Tr>
							<Table.Th scope="col">{t("pages.development.isolation.role", "Role")}</Table.Th>
							<Table.Th scope="col">{t("pages.development.isolation.provider", "Provider")}</Table.Th>
							<Table.Th scope="col">{t("pages.development.isolation.backend", "Backend")}</Table.Th>
							<Table.Th scope="col">{t("pages.development.isolation.level", "Level")}</Table.Th>
							<Table.Th scope="col">{t("pages.development.isolation.filesystem", "Filesystem")}</Table.Th>
							<Table.Th scope="col">{t("pages.development.isolation.network", "Network")}</Table.Th>
							<Table.Th scope="col">{t("pages.development.isolation.limits", "Resource limits")}</Table.Th>
							<Table.Th scope="col">{t("pages.development.isolation.readOnlyMounts", "Read-only mounts")}</Table.Th>
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{roles.map((role) => (
							<Table.Tr key={role.role} data-testid={`sandbox-isolation-row-${role.role}`}>
								<Table.Th scope="row">{role.role}</Table.Th>
								<Table.Td data-testid={`sandbox-isolation-provider-${role.role}`}>{role.provider}</Table.Td>
								<Table.Td data-testid={`sandbox-isolation-backend-${role.role}`}>{role.backend}</Table.Td>
								<Table.Td>
									<Badge color={levelColor(role.level)} data-testid={`sandbox-isolation-level-${role.role}`}>
										{levelLabel(t, role.level)}
									</Badge>
								</Table.Td>
								<Table.Td data-testid={`sandbox-isolation-filesystem-${role.role}`}>
									{yesNo(t, role.filesystemIsolation)}
								</Table.Td>
								<Table.Td data-testid={`sandbox-isolation-network-${role.role}`}>{yesNo(t, role.networkIsolation)}</Table.Td>
								<Table.Td data-testid={`sandbox-isolation-limits-${role.role}`}>{yesNo(t, role.resourceLimits)}</Table.Td>
								<Table.Td data-testid={`sandbox-isolation-readonly-${role.role}`}>{yesNo(t, role.readOnlyMounts)}</Table.Td>
							</Table.Tr>
						))}
					</Table.Tbody>
				</Table>
			</Table.ScrollContainer>

			{/*
			 * The measured reason, not a generic "unavailable". On a host without a working bubblewrap chain this
			 * sentence is the entire explanation an operator gets for why agent execution has no filesystem boundary,
			 * so it is shown rather than folded into a tooltip.
			 */}
			{reasons.length === 0 ? null : (
				<Stack gap="xs">
					{reasons.map((role) => (
						<Text key={role.role} size="xs" c="dimmed" data-testid={`sandbox-isolation-reason-${role.role}`}>
							{`${role.role}: ${role.filesystemIsolationUnavailableReason}`}
						</Text>
					))}
				</Stack>
			)}
		</SectionCard>
	);
}

function levelColor(level: string | undefined): string {
	if (level === "Isolated") {
		return "green";
	}

	return level === "Confined" ? "yellow" : "red";
}

function levelLabel(t: (key: string, fallback: string) => string, level: string | undefined): string {
	if (level === "Isolated") {
		return t("pages.development.isolation.levels.isolated", "Isolated");
	}
	if (level === "Confined") {
		return t("pages.development.isolation.levels.confined", "Confined");
	}

	return t("pages.development.isolation.levels.none", "None");
}

function yesNo(t: (key: string, fallback: string) => string, value: boolean | undefined): string {
	return value === true ? t("pages.development.isolation.yes", "Yes") : t("pages.development.isolation.no", "No");
}
