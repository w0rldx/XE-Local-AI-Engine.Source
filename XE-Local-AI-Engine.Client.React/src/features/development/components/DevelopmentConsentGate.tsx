import { Button, Checkbox, List, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconX } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { type ReactNode, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { isDevelopmentContainerProvider } from "@/features/development/models/DevelopmentModels";
import { useDevelopmentCapability } from "@/features/development/queries/useDevelopment";
import { useDevelopmentConsentStore } from "@/features/development/stores/DevelopmentConsentStore";

interface DevelopmentConsentGateProps {
	readonly children: ReactNode;
}

/**
 * The one-time disclosure an operator sees before Development Mode is usable, and the page behind it.
 *
 * Development Mode ships enabled, which means the first time anyone opens this route they are one click away from
 * having a model run their repository's build, test and lint commands. The notice exists so that click is informed.
 * It is a DISCLOSURE, not a control: acknowledging grants nothing, blocks nothing, and is recorded only in this
 * browser. Nothing here should read as protection.
 *
 * The copy is conditioned on the resolved sandbox provider, because the two providers put the operator in materially
 * different positions and a sentence true of one is false — in the unsafe direction — about the other. Within the
 * process provider it is conditioned again, in prose, on the host OS: the provider requests nothing the host cannot
 * deliver, so on Windows it does not fail closed, it silently degrades to no resource ceiling and no network
 * restriction, while on Linux the same code path enforces real containment. Saying only one of those would be
 * inaccurate on half the installs, so the notice says both and names which is which.
 *
 * Egress is conditioned on the same thing the backend conditions it on. The sandbox the agent's work runs in
 * asks for SandboxNetworkPolicy.None wherever the backend advertises SupportsNetworkPolicy
 * (DevelopmentWorkspaceProvider.ResolveAgentFacingNetworkPolicy) — which is Linux with a working `unshare` probe, and
 * the container provider — and Unrestricted where it does not, because a backend fails a confinement request it cannot
 * honour CLOSED and an unconditional denial would remove Development Mode from Windows rather than harden it. The one
 * sandbox that keeps egress by design is the engine's own warm restore against the BASE COMMIT, which runs before the
 * agent has written anything. So this copy says "denied where this node can enforce it", names the exception, and
 * points at the Sandbox isolation panel for which of the two was actually served — it must not promise denial, and it
 * must not repeat the obsolete claim that nothing restricts what a command can reach.
 *
 * Rendered as a gate rather than an overlay on a live page: an operator who has not read it should not be able to
 * start an attempt behind it.
 */
export function DevelopmentConsentGate({ children }: DevelopmentConsentGateProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const acknowledged = useDevelopmentConsentStore((state) => state.acknowledged);
	const acknowledge = useDevelopmentConsentStore((state) => state.actions.acknowledge);
	const [confirmed, setConfirmed] = useState(false);
	// Shares the page's capability query (react-query dedupes by key), so the notice describes the provider the
	// backend actually resolved rather than the one this screen assumes.
	const capabilityQuery = useDevelopmentCapability();
	const containerProvider = isDevelopmentContainerProvider(capabilityQuery.data?.sandboxProvider);

	// Only ask once there is something to consent to. While the capability is unresolved — still loading, errored, or
	// Development disabled on this node — the page renders and states that for itself; a disclosure about what
	// Development Mode executes would be describing something that cannot run.
	if (acknowledged || capabilityQuery.data?.enabled !== true) {
		return <>{children}</>;
	}

	return (
		<DialogShell
			opened={true}
			onClose={() => navigate({ to: "/" })}
			title={t("pages.development.consent.title", "Before you use Development Mode")}
			showCloseButton={false}
			enableFullScreenToggle={false}
			closeOnClickOutside={false}
			closeOnEscape={false}
			data-testid="development-consent-dialog"
			footer={
				<>
					<Button
						variant="subtle"
						leftSection={<IconX size={16} />}
						onClick={() => navigate({ to: "/" })}
						data-testid="development-consent-decline"
					>
						{t("pages.development.consent.decline", "Not now")}
					</Button>
					<Button
						leftSection={<IconCheck size={16} />}
						disabled={!confirmed}
						onClick={() => acknowledge()}
						data-testid="development-consent-accept"
					>
						{t("pages.development.consent.accept", "Continue to Development Mode")}
					</Button>
				</>
			}
		>
			<Stack gap="md" px="md" pb="md">
				<Text size="sm">
					{t(
						"pages.development.consent.intro",
						"Development Mode lets a model change files in a managed worktree and runs your repository's own build, test and lint commands on this machine. Read this once before you start.",
					)}
				</Text>

				<List size="sm" spacing="xs" icon={<IconAlertTriangle size={16} />} data-testid="development-consent-terms">
					{containerProvider ? (
						<>
							<List.Item>
								{t(
									"pages.development.consent.container",
									"On this node those commands run inside a container: read-only root filesystem, all capabilities dropped, no host namespaces, and only the managed worktree and runtime directories mounted.",
								)}
							</List.Item>
							<List.Item>
								{t(
									"pages.development.consent.containerNetwork",
									"Egress is denied for the sandbox the agent works in: the container provider can enforce it. Only the engine's own dependency restore, run against your base commit before the agent starts, is given the network.",
								)}
							</List.Item>
						</>
					) : (
						<>
							<List.Item>
								{t(
									"pages.development.consent.processUser",
									"On this node those commands run as the signed-in user account that runs the engine — with that account's access to your files, not just the repository.",
								)}
							</List.Item>
							<List.Item>
								{t(
									"pages.development.consent.processNetwork",
									"Egress is denied for the sandbox the agent works in wherever this node can enforce it — Linux with a working sandbox network probe, or a container provider. Where it cannot, such as Windows, those commands reach the network unrestricted. Only the engine's own dependency restore, run against your base commit before the agent starts, is given the network. The Sandbox isolation panel reports which posture this node served.",
								)}
							</List.Item>
							<List.Item>
								{t(
									"pages.development.consent.processLimits",
									"CPU, memory and process-count ceilings are requested for these commands wherever this node can enforce them (the isolation panel shows whether it does): all logical cores, 75% of memory with a 4 GB floor, and 4096 processes by default, and an operator can override them. Where the host cannot impose them — Windows — no ceiling applies and a runaway command is bounded only by its timeout and the machine.",
								)}
							</List.Item>
						</>
					)}
					<List.Item>
						{t("pages.development.consent.trust", "Register only repositories you trust. Repository code executes either way.")}
					</List.Item>
				</List>

				<Text size="xs" c="dimmed">
					{t(
						"pages.development.consent.disclosureNote",
						"This notice is a disclosure, not a protection. Acknowledging it is recorded in this browser only and changes nothing about what a Development run is allowed to do.",
					)}
				</Text>

				<Checkbox
					checked={confirmed}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setConfirmed(checked);
					}}
					label={t("pages.development.consent.checkbox", "I understand what Development Mode executes on this machine.")}
					data-testid="development-consent-checkbox"
				/>
			</Stack>
		</DialogShell>
	);
}
