import { Button, ScrollArea, Stack, Text } from "@mantine/core";
import type { ModalProps } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconArrowsDiagonal } from "@tabler/icons-react";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

export interface IExpandableTextFieldProps {
	/** Label shown before the preview text and used as the default dialog title. */
	label: string;
	/** Full text value. The preview is clamped; the dialog renders it in full. */
	value: string;
	/** Number of lines shown in the clamped preview. Defaults to 4. */
	previewLineClamp?: number;
	/** Dialog title. Defaults to the label. */
	dialogTitle?: string;
	/** Label for the expand button. Defaults to "Show full". */
	expandLabel?: string;
	/** Dialog size forwarded to the underlying Modal. Defaults to "xl". */
	dialogSize?: ModalProps["size"];
}

/**
 * Renders a potentially long text value as a clamped, minimized preview with an
 * expand button that opens the full value in a scrollable {@link DialogShell}.
 *
 * Reusable across the app for any text that can grow large (model licenses,
 * templates, system prompts, …) so it never blows out the surrounding layout.
 */
export function ExpandableTextField({
	label,
	value,
	previewLineClamp = 4,
	dialogTitle,
	expandLabel = "Show full",
	dialogSize = "xl",
}: IExpandableTextFieldProps) {
	const [opened, { open, close }] = useDisclosure(false);

	return (
		<Stack gap={4}>
			<Text size="sm" c="dimmed" lineClamp={previewLineClamp} style={{ whiteSpace: "pre-wrap" }}>
				{label}: {value}
			</Text>
			<Button
				variant="subtle"
				size="compact-sm"
				leftSection={<IconArrowsDiagonal size={14} />}
				onClick={open}
				style={{ alignSelf: "flex-start" }}
			>
				{expandLabel}
			</Button>
			<DialogShell opened={opened} onClose={close} title={dialogTitle ?? label} size={dialogSize}>
				<ScrollArea.Autosize mah="60vh" px="md" pb="md">
					<Text size="sm" style={{ whiteSpace: "pre-wrap", wordBreak: "break-word" }}>
						{value}
					</Text>
				</ScrollArea.Autosize>
			</DialogShell>
		</Stack>
	);
}
