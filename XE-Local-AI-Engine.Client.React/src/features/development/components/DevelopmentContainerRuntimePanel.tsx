import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCircleCheck, IconShieldLock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsDevelopmentV1DevelopmentContainerRuntimeResponse as ContainerRuntimeStatus } from "@/core/api/generated/types.gen";
import { isDevelopmentContainerProvider } from "@/features/development/models/DevelopmentModels";

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
	/**
	 * The sandbox provider actually resolved for this node. Decides whether this preflight describes the runtime in
	 * force or one that Development Mode has not moved to yet — the two are opposite claims and cannot both be static.
	 */
	readonly sandboxProvider?: string;
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
	sandboxProvider,
}: DevelopmentContainerRuntimePanelProps) {
	const { t } = useTranslation();
	const containerProvider = isDevelopmentContainerProvider(sandboxProvider);

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
				 * Read from the resolved provider, not asserted. This sentence used to say container execution was off
				 * regardless — which was measured false with the container provider live and a container demonstrably
				 * running, on a screen whose own banner said the runtime was ready.
				 */}
				<Text size="xs" c="dimmed" data-testid="development-container-runtime-not-yet-in-use">
					{containerProvider
						? t(
								"pages.development.containerRuntime.inUse",
								"Development commands on this node execute inside this container runtime. A failure reported here stops runs; it does not fall back to the host.",
							)
						: t(
								"pages.development.containerRuntime.notYetInUse",
								"Container-backed execution is not switched on yet. Today's runs still use the supervised process sandbox; this preflight reports whether the container runtime will be available when they move.",
							)}
				</Text>
			</Stack>
		</Alert>
	);
}
