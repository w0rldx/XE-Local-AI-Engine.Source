import { Button, Loader, Stack, Text } from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { useRestartPoll } from "@/core/ui/hooks/useRestartPoll";
import { AppUpdateBusyDialog } from "@/features/app-update/components/AppUpdateBusyDialog";
import { type AppUpdateBusyItem, parseAppUpdateBusy } from "@/features/app-update/models/AppUpdateBusy";
import { useApplyAppUpdate, useAppUpdateStatus, useProbeAppUpdateStatus } from "@/features/app-update/queries/useAppUpdate";

type ApplyState = "idle" | "applying" | "error";

/**
 * Owns the full ready-state update UI. Keeping this component mounted while an update is applying is load-bearing:
 * the old host clears its cached availability before exit, but restart polling must continue until the new version
 * answers. Calls `applyAppUpdate`, then waits through `useRestartPoll` for the side-effect-free version endpoint to
 * report the installed version.
 */
export function AppUpdateButton() {
	const { t } = useTranslation();
	const { data: status } = useAppUpdateStatus();
	const applyMutation = useApplyAppUpdate();
	const probeStatusMutation = useProbeAppUpdateStatus();

	const [applyState, setApplyState] = useState<ApplyState>("idle");
	// The running work a refused apply listed; non-null opens the confirm dialog that re-sends with force.
	const [busyItems, setBusyItems] = useState<AppUpdateBusyItem[] | null>(null);
	const restartPoll = useRestartPoll();

	const availableVersion = status?.availableVersion ?? null;
	const shouldRender = status?.isDesktop === true && status.isConfigured === true;

	if (!shouldRender) {
		return null;
	}

	async function handleApply(force: boolean) {
		if (!availableVersion) {
			setApplyState("error");
			return;
		}
		setApplyState("applying");
		try {
			const result = await applyMutation.mutateAsync({ body: { force } });
			setBusyItems(null);
			if (!result.applying) {
				setApplyState("idle");
				return;
			}
			// The apply's live re-check can install a newer release than the cached offer; wait for what it installs.
			const expectedVersion = result.targetVersion ?? availableVersion;
			setApplyState("idle");
			restartPoll.start(async () => (await probeStatusMutation.mutateAsync()).currentVersion === expectedVersion);
		} catch (error: unknown) {
			const busy = parseAppUpdateBusy(error);
			if (busy !== null) {
				setBusyItems(busy);
				setApplyState("idle");
				return;
			}
			setBusyItems(null);
			setApplyState("error");
		}
	}

	if (restartPoll.status === "restarting") {
		return (
			<Stack gap="xs" align="center">
				<Loader size="sm" />
				<Text size="sm" c="dimmed">
					{t("pages.about.appUpdate.restarting")}
				</Text>
			</Stack>
		);
	}

	if (applyState === "error" || restartPoll.status === "timedOut") {
		return (
			<Text size="sm" c="red">
				{t("pages.about.appUpdate.applyError")}
			</Text>
		);
	}

	if (applyState === "idle" && !status.updateAvailable) {
		return (
			<Text size="sm" c="dimmed">
				{t("pages.about.appUpdate.upToDate")}
			</Text>
		);
	}

	return (
		<>
			<Button
				variant="filled"
				leftSection={<IconRefresh size={16} />}
				loading={applyState === "applying" && busyItems === null}
				onClick={() => {
					handleApply(false).catch((error: unknown) => console.error("apply failed", error));
				}}
			>
				{t("pages.about.appUpdate.updateNow")}
			</Button>
			<AppUpdateBusyDialog
				busyItems={busyItems}
				isPending={applyState === "applying"}
				onClose={() => setBusyItems(null)}
				onConfirm={() => {
					handleApply(true).catch((error: unknown) => console.error("apply failed", error));
				}}
			/>
		</>
	);
}
