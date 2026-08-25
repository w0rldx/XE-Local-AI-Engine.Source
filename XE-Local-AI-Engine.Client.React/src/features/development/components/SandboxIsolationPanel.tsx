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
 * Reported per role because the posture is per role, on two axes at once. Provider selection is per feature —
 * AgentHome, Development Mode and work sessions each resolve their own — and the ROLE'S OWN declaration decides what it
 * asks that provider for, which is why `run_python` gets a row despite sharing AgentHome's provider instance: it is the
 * one workload that asks for a filesystem boundary, so its row and AgentHome's differ on the same backend. The backend
 * derives every value as that declaration intersected with the provider's advertised capabilities, so nothing here can
 * claim a boundary the launch path would not enforce OR one the role never requested.
 *
 * A "No" in the Filesystem or Resource-limits column therefore has two meanings, and the reason lines under the table
 * are where they are told apart: the role does not ask (nothing to fix here — whether it should ask is an operator
 * decision), or it asks and this host cannot serve it (the measured probe reason).
 *
 * Every value carries a test id, for the same reason the container-runtime panel's do: a test that only asserted the
 * table rendered would pass against a table full of the wrong answers, on the one surface where that matters most.
 */
export function SandboxIsolationPanel({ roles }: SandboxIsolationPanelProps) {
	const { t } = useTranslation();

	if (roles === undefined || roles.length === 0) {
		return null;
	}

	// One line per role PER AXIS. Two axes can be "No" for different reasons at once — Development Mode on this Linux
	// box has neither a filesystem boundary nor a ceiling, and the two sentences are not interchangeable — so they are
	// flattened rather than folded into one. The axis label reuses the column header key, so no new copy to translate.
	const reasons = roles.flatMap((role) => [
		{
			key: `${role.role}-filesystem`,
			testId: `sandbox-isolation-reason-${role.role}`,
			label: t("pages.development.isolation.filesystem", "Filesystem"),
			text: role.filesystemIsolationUnavailableReason ?? "",
			role: role.role ?? "",
		},
		{
			key: `${role.role}-limits`,
			testId: `sandbox-isolation-limits-reason-${role.role}`,
			label: t("pages.development.isolation.limits", "Resource limits"),
			text: role.resourceLimitsUnavailableReason ?? "",
			role: role.role ?? "",
		},
	]).filter((reason) => reason.text !== "");

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
					{reasons.map((reason) => (
						<Text key={reason.key} size="xs" c="dimmed" data-testid={reason.testId}>
							{`${reason.role} · ${reason.label}: ${reason.text}`}
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
