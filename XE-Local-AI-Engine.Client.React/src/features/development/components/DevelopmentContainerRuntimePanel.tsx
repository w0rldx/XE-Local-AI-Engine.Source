import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconShieldLock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentContainerRuntimeResponse as ContainerRuntimeStatus } from "@/core/api/generated/types.gen";

interface DevelopmentContainerRuntimePanelProps {
	/**
	 * Null when Development Mode is not running on the container provider — the backend reports no container-runtime
	 * block at all in that case, rather than a preflight for a dependency this node does not have. Undefined while the
	 * capability query is still in flight. Both render nothing.
	 */
	readonly runtime: ContainerRuntimeStatus | null | undefined;
	readonly onConfirm: (daemonId: string) => void;
	readonly confirming: boolean;
	readonly confirmError?: string;
}

/**
 * The container-runtime preflight, as an operator sees it.
 *
 * ADR 0004 rules out an unisolated fallback: a node without a working container runtime does not get a degraded
 * Development Mode, it gets none. That makes this panel the whole user experience of that failure, so it always shows
 * the backend's message rather than a generic "unavailable", and it shows the machine-readable status alongside it so
 * a support conversation can name the case rather than describe it.
 *
 * Every value carries its own test id. A test that only asserted the panel rendered would pass against an empty
 * message and the wrong status, which is exactly the false green this surface exists to prevent.
 */
export function DevelopmentContainerRuntimePanel({
	runtime,
	onConfirm,
	confirming,
	confirmError,
}: DevelopmentContainerRuntimePanelProps) {
	const { t } = useTranslation();

	if (!runtime) {
		return null;
	}

	const ready = runtime.ready === true;
	const needsConfirmation = runtime.requiresOperatorConfirmation === true;
	const observedDaemonId = runtime.observedDaemon?.daemonId;

	return (
		<Alert
			color={ready ? "green" : needsConfirmation ? "yellow" : "red"}
			icon={ready ? <IconCircleCheck size={16} /> : needsConfirmation ? <IconShieldLock size={16} /> : <IconAlertTriangle size={16} />}
			title={t("pages.development.containerRuntime.title", "Container runtime")}
			data-testid="development-container-runtime"
		>
			<Stack gap="xs">
				<Text size="sm" data-testid="development-container-runtime-status">
					{runtime.status ?? "unknown"}
				</Text>

				<Text size="sm" data-testid="development-container-runtime-message">
					{runtime.message ?? ""}
				</Text>

				{runtime.endpoint ? (
					<Text size="xs" c="dimmed" data-testid="development-container-runtime-endpoint">
						{runtime.endpoint}
					</Text>
				) : null}

				{runtime.pinnedDaemon?.daemonId ? (
					<Text size="xs" c="dimmed" data-testid="development-container-runtime-pinned-daemon">
						{runtime.pinnedDaemon.daemonId}
					</Text>
				) : null}

				{observedDaemonId ? (
					<Text size="xs" c="dimmed" data-testid="development-container-runtime-observed-daemon">
						{observedDaemonId}
					</Text>
				) : null}

				{/*
				 * Offered only for the one state a confirmation resolves. Showing it elsewhere would invite an operator
				 * to "approve" their way past a missing daemon, which no approval can fix.
				 */}
				{needsConfirmation && observedDaemonId ? (
					<Group gap="sm">
						<Button
							size="xs"
							color="yellow"
							loading={confirming}
							onClick={() => onConfirm(observedDaemonId)}
							data-testid="development-container-runtime-confirm"
						>
							{t("pages.development.containerRuntime.confirm", "Confirm this container runtime")}
						</Button>
					</Group>
				) : null}

				{confirmError ? (
					<Text size="xs" c="red" data-testid="development-container-runtime-confirm-error">
						{confirmError}
					</Text>
				) : null}

				{/*
				 * Phase 1 honesty. The provider exists and is verified, but Development Mode still executes through the
				 * supervised process sandbox until per-feature provider selection lands, and an operator reading a
				 * container-runtime banner would otherwise reasonably assume today's runs already use it.
				 */}
				<Text size="xs" c="dimmed" data-testid="development-container-runtime-not-yet-in-use">
					{t(
						"pages.development.containerRuntime.notYetInUse",
						"Container-backed execution is not switched on yet. Today's runs still use the supervised process sandbox; this preflight reports whether the container runtime will be available when they move.",
					)}
				</Text>
			</Stack>
		</Alert>
	);
}
