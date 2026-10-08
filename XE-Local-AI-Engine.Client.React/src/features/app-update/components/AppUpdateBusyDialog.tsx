import { Button, Group, List, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { type AppUpdateBusyItem, busyKindLabel } from "@/features/app-update/models/AppUpdateBusy";

interface AppUpdateBusyDialogProps {
	/** The running work the node reported; null keeps the dialog closed. */
	busyItems: AppUpdateBusyItem[] | null;
	isPending: boolean;
	onClose: () => void;
	onConfirm: () => void;
}

/** Asks before an update restart stops the training runs, downloads, builds or responses that are running now. */
export function AppUpdateBusyDialog({ busyItems, isPending, onClose, onConfirm }: AppUpdateBusyDialogProps) {
	const { t } = useTranslation();
	return (
		<DialogShell
			opened={busyItems !== null}
			onClose={onClose}
			title={t("pages.about.appUpdate.busy.title")}
			size="md"
			// Opens over the still-open About dialog; `raised` puts it on top by declaration, not portal order.
			raised={true}
			data-testid="app-update-busy-dialog"
		>
			<Stack gap="md">
				<Text>{t("pages.about.appUpdate.busy.description")}</Text>
				<List size="sm">
					{(busyItems ?? []).map((item) => (
						<List.Item key={`${item.kind}:${item.displayName ?? ""}`}>
							{item.displayName ? `${busyKindLabel(t, item.kind)}: ${item.displayName}` : busyKindLabel(t, item.kind)}
						</List.Item>
					))}
				</List>
				<Group justify="flex-end">
					<Button variant="default" onClick={onClose}>
						{t("common.cancel")}
					</Button>
					<Button color="red" loading={isPending} onClick={onConfirm} data-testid="app-update-busy-confirm">
						{t("pages.about.appUpdate.busy.confirm")}
					</Button>
				</Group>
			</Stack>
		</DialogShell>
	);
}
