import { Switch } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { systemNotificationsSupported } from "@/core/operations/models/JobAlerts";
import { useJobNotificationStore } from "@/core/operations/stores/JobNotificationStore";

// The per-browser opt-in for OS notifications when a long job finishes while the tab is hidden. Turning it on is what
// asks the browser for permission, so the prompt only ever follows the user's own click. A refused or blocked
// permission leaves the switch off and says where to change it; the toast and the tab-title count need no permission.
export function JobNotificationsSwitch() {
	const { t } = useTranslation();
	const wanted = useJobNotificationStore((state) => state.systemNotifications);
	const { setSystemNotifications } = useJobNotificationStore((state) => state.actions);
	const supported = systemNotificationsSupported();
	const [permission, setPermission] = useState<NotificationPermission>(() => (supported ? Notification.permission : "denied"));

	const handleChange = async (checked: boolean): Promise<void> => {
		if (!checked || !supported) {
			setSystemNotifications(false);
			return;
		}
		const granted = Notification.permission === "default" ? await Notification.requestPermission() : Notification.permission;
		setPermission(granted);
		setSystemNotifications(granted === "granted");
	};

	const description = !supported
		? t("operations.notifications.unsupported", "This browser cannot show system notifications.")
		: permission === "denied"
			? t(
					"operations.notifications.blocked",
					"This browser blocks notifications from this app. Allow them in the browser's site settings, then turn this on.",
				)
			: t(
					"operations.notifications.description",
					"When a download, indexing, training, image or scheduled job finishes while this tab is in the background, also show a system notification. Stored in this browser only.",
				);

	return (
		<Switch
			label={t("operations.notifications.label", "Notify me when long jobs finish (OS notification)")}
			description={description}
			checked={wanted && permission === "granted"}
			disabled={!supported}
			onChange={(event) => {
				handleChange(event.currentTarget.checked).catch(() => setSystemNotifications(false));
			}}
			data-testid="job-notifications-switch"
		/>
	);
}
