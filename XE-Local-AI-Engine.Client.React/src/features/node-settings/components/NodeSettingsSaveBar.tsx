import { Button, Group, Paper, Stack, Text } from "@mantine/core";
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
	readonly compact: boolean;
}

// The page's single save point, sticky at the bottom of the scroll area. The status line is a polite live region, so a
// screen reader hears the counts change as fields are edited.
export function NodeSettingsSaveBar({
	unsavedCount,
	restartCount,
	canSave,
	canReset,
	isSaving,
	onSave,
	onReset,
	compact,
}: Props) {
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

	const statusText = (
		<Text
			size={compact ? "xs" : "sm"}
			lineClamp={2}
			style={{ minWidth: 0 }}
			role="status"
			aria-live="polite"
			data-testid="node-settings-save-bar-status"
		>
			{status}
		</Text>
	);
	const buttons = (
		<>
			<Button
				variant="default"
				size={compact ? "xs" : "sm"}
				leftSection={compact ? undefined : <IconRestore size={16} />}
				onClick={onReset}
				disabled={!canReset}
				data-testid="node-settings-reset-button"
			>
				{t("pages.nodeSettings.saveBar.reset", "Reset")}
			</Button>
			<Button
				size={compact ? "xs" : "sm"}
				leftSection={compact ? undefined : <IconDeviceFloppy size={16} />}
				onClick={onSave}
				loading={isSaving}
				disabled={!canSave}
				data-testid="node-settings-save-button"
			>
				{t("pages.nodeSettings.saveBar.save", "Save changes")}
			</Button>
		</>
	);

	return (
		<Paper
			withBorder={true}
			shadow="sm"
			radius="md"
			p={compact ? "xs" : "sm"}
			pos="sticky"
			bottom={0}
			style={{ zIndex: 2 }}
			data-testid="node-settings-save-bar"
		>
			{/* Wide: one row, the status text gives way (two lines at most). Compact: the status line sits above the
			    buttons, because one row squeezed a long German status to a few letters; the buttons drop their icons and
			    shrink so the two rows stay short. */}
			{compact ? (
				<Stack gap={4}>
					{statusText}
					<Group justify="flex-end" gap="xs" wrap="nowrap">
						{buttons}
					</Group>
				</Stack>
			) : (
				<Group justify="space-between" gap="sm" wrap="nowrap">
					{statusText}
					<Group gap="sm" wrap="nowrap" style={{ flexShrink: 0 }}>
						{buttons}
					</Group>
				</Group>
			)}
		</Paper>
	);
}
