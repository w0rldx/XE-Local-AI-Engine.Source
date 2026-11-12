import { Badge, Loader } from "@mantine/core";
import { useEffect, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";

import { hasDegradedHub, subscribeDegradedHubs } from "@/core/api/signalr/SharedHubConnection";

// A drop shorter than this (a sleep-wake blip SignalR heals at once) shows nothing.
const HUB_RECONNECTING_CHIP_DELAY_MS = 2_000;

// One header chip for every shared live-update hub: while any held hub is reconnecting or waiting to restart, the page
// it feeds has stopped updating. The chat hub has its own chip on its own connection.
export function HubConnectionStatusChip() {
	const { t } = useTranslation();
	const degraded = useSyncExternalStore(subscribeDegradedHubs, hasDegradedHub);
	const [visible, setVisible] = useState(false);

	useEffect(() => {
		if (!degraded) {
			setVisible(false);
			return;
		}
		const timer = setTimeout(() => setVisible(true), HUB_RECONNECTING_CHIP_DELAY_MS);
		return () => clearTimeout(timer);
	}, [degraded]);

	return (
		<div role="status" aria-live="polite" className="flex-shrink-0">
			{visible ? (
				<Badge
					variant="light"
					color="yellow"
					leftSection={<Loader size={10} color="yellow" />}
					data-testid="hub-connection-status-chip"
				>
					{t("components.headerBar.liveUpdatesReconnecting", "Live updates reconnecting…")}
				</Badge>
			) : null}
		</div>
	);
}
