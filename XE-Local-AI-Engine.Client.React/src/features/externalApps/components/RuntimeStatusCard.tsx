import { Alert, Button, Card, Group, Skeleton, Stack, Text } from "@mantine/core";
import type { TFunction } from "i18next";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import {
	type ContainerRuntimeStatus,
	externalAppCapabilityLabel,
	type ExternalAppRuntimeResponse,
	toContainerRuntimeStatus,
} from "@/features/externalApps/models/ExternalAppModels";
import { useRefreshExternalAppRuntime } from "@/features/externalApps/queries/useExternalApps";

interface RuntimeStatusCardProps {
	readonly runtime: ExternalAppRuntimeResponse | undefined;
	readonly isLoading: boolean;
	readonly "data-testid"?: string;
}

const statusColors: Record<ContainerRuntimeStatus, "green" | "yellow" | "orange" | "gray"> = {
	Ready: "green",
	DaemonUnreachable: "yellow",
	PermissionDenied: "yellow",
	ApiVersionTooOld: "yellow",
	// Not a fault of the machine: something changed and only the operator can say whether that was expected.
	DaemonIdentityChanged: "orange",
	NotConfigured: "gray",
	ProbeFailed: "yellow",
};

/**
 * The one place the container runtime is named. Everything else in this feature talks about applications.
 *
 * Two server fields drive it rather than a status literal: `requiresOperatorConfirmation` gates the trust action — a
 * security-relevant decision gets no second source of truth in the client — and `message` is appended after the local
 * hint, which stays because it says what to DO.
 */
export function RuntimeStatusCard({ runtime, isLoading, "data-testid": testId }: RuntimeStatusCardProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const refresh = useRefreshExternalAppRuntime();

	const status = toContainerRuntimeStatus(runtime?.status);
	const statusLabel = t(`pages.externalApps.runtime.status.${status}`);
	const observedDaemonId = runtime?.observedDaemon?.daemonId ?? undefined;
	const missingCapabilities = missingCapabilityNames(runtime, t);
	// Containers carrying this owner label but ANOTHER install id. The node never touches them (R2-26), so the count
	// is information the operator has to act on, and a card that swallows it says XE owns every labelled container on
	// the machine. It is read from the REFRESH response first: `GET external-apps/runtime` is a pure read that
	// reports 0 on purpose, because the count is the reconciler's observation and a cached one would claim a foreign
	// container is there when it is not. So the number appears once the operator has pressed Check again.
	const foreignInstallContainers = refresh.data?.foreignInstallContainers ?? runtime?.foreignInstallContainers ?? 0;

	const check = (acknowledgeDaemonId?: string): void => {
		refresh.mutate(
			{ body: { acknowledgeDaemonId } },
			{
				onSuccess: () => toast.success(t("pages.externalApps.runtime.checked")),
				onError: (error) => toast.error(apiErrorMessage(error, t("pages.externalApps.runtime.checkFailed"))),
			},
		);
	};

	// The operator approves the identity they were SHOWN, so the id is both interpolated into the confirmation and
	// sent with the request; a daemon that changed again in between is refused by the node with a 400.
	const trust = (daemonId: string): void => {
		confirm({
			title: t("pages.externalApps.runtime.acknowledgeIdentityTitle"),
			description: t("pages.externalApps.runtime.acknowledgeIdentityDescription", { daemonId }),
		})
			.then((accepted) => {
				if (accepted) {
					check(daemonId);
				}
			})
			// A dismissed confirmation must not surface as an unhandled rejection; declining simply sends nothing.
			.catch(() => undefined);
	};

	if (isLoading) {
		return <Skeleton height={120} radius="md" data-testid={testId ?? "external-app-runtime-card"} />;
	}

	return (
		<Card withBorder={true} padding="md" radius="md" data-testid={testId ?? "external-app-runtime-card"}>
			<Stack gap="sm">
				<Group justify="space-between" wrap="nowrap">
					<Group gap="sm">
						<Text fw={600}>{t("pages.externalApps.runtime.title")}</Text>
						<StatusBadge
							color={statusColors[status]}
							label={statusLabel}
							aria-label={statusLabel}
							data-testid="external-app-runtime-status"
						/>
					</Group>
					<Button
						variant="default"
						size="xs"
						loading={refresh.isPending}
						onClick={() => check()}
						data-testid="external-app-runtime-refresh"
					>
						{t("pages.externalApps.runtime.refresh")}
					</Button>
				</Group>

				{runtime?.provider ? (
					<Text size="sm" c="dimmed">
						{t("pages.externalApps.runtime.provider", { provider: runtime.provider })}
					</Text>
				) : null}

				{/* The last known state is still shown below: a transient status must not be repainted as a failure
				    just because the probe could not reach the daemon this minute. */}
				{runtime?.available === false ? (
					<Text size="sm" data-testid="external-app-runtime-unavailable">
						{t("pages.externalApps.runtime.unavailable")}
					</Text>
				) : null}

				{status === "Ready" ? (
					<Text size="sm">{t(`pages.externalApps.runtime.hint.${status}`)}</Text>
				) : (
					<Alert color="yellow" data-testid="external-app-runtime-alert">
						<Stack gap={4}>
							<Text size="sm">{t(`pages.externalApps.runtime.hint.${status}`)}</Text>
							{runtime?.message ? <Text size="sm">{runtime.message}</Text> : null}
							{missingCapabilities.length > 0 ? (
								<Text size="sm" data-testid="external-app-runtime-missing-capabilities">
									{t("pages.externalApps.runtime.missingCapabilities", {
										capabilities: missingCapabilities.join(", "),
									})}
								</Text>
							) : null}
						</Stack>
					</Alert>
				)}

				{foreignInstallContainers > 0 ? (
					<Text size="sm" c="dimmed" data-testid="external-app-runtime-foreign-containers">
						{t("pages.externalApps.runtime.foreignInstallContainers", { count: foreignInstallContainers })}
					</Text>
				) : null}

				{runtime?.requiresOperatorConfirmation === true && observedDaemonId ? (
					<Group>
						<Button
							color="orange"
							size="xs"
							loading={refresh.isPending}
							onClick={() => trust(observedDaemonId)}
							data-testid="external-app-runtime-trust"
						>
							{t("pages.externalApps.runtime.acknowledgeIdentity")}
						</Button>
					</Group>
				) : null}
			</Stack>
		</Card>
	);
}

/**
 * The capability flags the node reported as false, read straight off the resolution rather than inferred from the
 * status, and translated: the wire carries the flag NAMES, and camel-splitting `loopbackPortPublishing` into
 * "loopback port publishing" puts an identifier in front of an operator and calls it English.
 *
 * A resolution that granted NOTHING reports nothing here: the node zeroes the whole bag whenever the runtime is not
 * ready, so listing all nine flags would tell an operator with a stopped daemon that nine separate features are
 * missing. The hint above the list already says what to do in that case.
 */
function missingCapabilityNames(runtime: ExternalAppRuntimeResponse | undefined, t: TFunction): readonly string[] {
	const flags = Object.entries(runtime?.capabilities ?? {});
	if (!flags.some(([, granted]) => granted === true)) {
		return [];
	}
	return flags.filter(([, granted]) => granted === false).map(([name]) => externalAppCapabilityLabel(name, t));
}
