import { Button, Group, Paper, Text } from "@mantine/core";
import { IconDeviceFloppy, IconRestore } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

interface Props {
	readonly unsavedCount: number;
	readonly restartCount: number;
	readonly canSave: boolean;
	readonly canReset: boolean;
	readonly isSaving: boolean;
	readonly onSave: () => void;
	readonly onReset: () => void;
}

// The page's single save point, sticky at the bottom of the scroll area. The status line is a polite live region, so a
// screen reader hears the counts change as fields are edited.
export function NodeSettingsSaveBar({ unsavedCount, restartCount, canSave, canReset, isSaving, onSave, onReset }: Props) {
	const { t } = useTranslation();
	const status =
		unsavedCount === 0
			? t("pages.nodeSettings.saveBar.clean", "No unsaved changes")
			: [
					t("pages.nodeSettings.saveBar.unsaved", "{{count}} unsaved changes", { count: unsavedCount }),
					restartCount > 0 ? t("pages.nodeSettings.saveBar.restart", "{{count}} need a restart", { count: restartCount }) : null,
				]
					.filter((part) => part !== null)
					.join(" · ");

	return (
		<Paper
			withBorder={true}
			shadow="sm"
			radius="md"
			p="sm"
			pos="sticky"
			bottom={0}
			style={{ zIndex: 2 }}
			data-testid="node-settings-save-bar"
		>
			<Group justify="space-between" gap="sm">
				<Text size="sm" role="status" aria-live="polite" data-testid="node-settings-save-bar-status">
					{status}
				</Text>
				<Group gap="sm">
					<Button
						variant="default"
						leftSection={<IconRestore size={16} />}
						onClick={onReset}
						disabled={!canReset}
						data-testid="node-settings-reset-button"
					>
						{t("pages.nodeSettings.saveBar.reset", "Reset")}
					</Button>
					<Button
						leftSection={<IconDeviceFloppy size={16} />}
						onClick={onSave}
						loading={isSaving}
						disabled={!canSave}
						data-testid="node-settings-save-button"
					>
						{t("pages.nodeSettings.saveBar.save", "Save changes")}
					</Button>
				</Group>
			</Group>
		</Paper>
	);
}
