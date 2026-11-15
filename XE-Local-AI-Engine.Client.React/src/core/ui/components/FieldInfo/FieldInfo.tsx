import { ActionIcon, getDefaultZIndex, Stack, Text, Tooltip } from "@mantine/core";
import { IconInfoCircle } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import classes from "./FieldInfo.module.css";

interface FieldInfoProps {
	/** The field's visible name: the card title and part of the button's accessible name. */
	readonly label: string;
	/** The card body; a blank line starts a new paragraph. */
	readonly text: string;
	readonly testId?: string;
}

const HOVER_AND_FOCUS_AND_TOUCH = { hover: true, focus: true, touch: true } as const;

// An icon-only (i) whose read-only card explains a tunable field. It opens on hover, on keyboard focus and on touch;
// clicking does nothing, and the card never takes the pointer.
export function FieldInfo({ label, text, testId }: FieldInfoProps) {
	const { t } = useTranslation();
	return (
		<Tooltip
			label={
				<Stack gap={6}>
					<Text size="sm" fw={600}>
						{label}
					</Text>
					{text.split(/\n\s*\n/).map((paragraph) => (
						<Text key={paragraph} size="sm">
							{paragraph}
						</Text>
					))}
				</Stack>
			}
			events={HOVER_AND_FOCUS_AND_TOUCH}
			openDelay={300}
			multiline={true}
			w={300}
			withArrow={true}
			// Opens beside the field; where the right side lacks room (narrow screens, right-hand columns) it flips above.
			position="right"
			middlewares={{ flip: { fallbackPlacements: ["top-end", "top", "bottom-end"] }, shift: true }}
			// Above every dialog layer, including a raised DialogShell, so a field inside a modal shows its card.
			zIndex={getDefaultZIndex("max")}
		>
			<ActionIcon
				variant="subtle"
				color="gray"
				size="sm"
				aria-label={t("common.fieldInfo.open", "About {{field}}", { field: label })}
				data-testid={testId}
			>
				<IconInfoCircle size={16} />
			</ActionIcon>
		</Tooltip>
	);
}

interface FieldWithInfoProps extends Partial<Pick<FieldInfoProps, "text">>, Omit<FieldInfoProps, "text"> {
	/** One Mantine input (NumberInput, Select, TextInput, Textarea, Switch, …) with its own `label`. */
	readonly children: ReactNode;
}

// Puts a FieldInfo at the right end of a Mantine input's label line WITHOUT placing it inside the <label>: a button
// there would join the input's accessible name and a click on it would activate the label (focus the input, toggle a
// Switch). The input keeps its label exactly as before. Without a text the input renders alone.
export function FieldWithInfo({ label, text, testId, children }: FieldWithInfoProps) {
	if (text === undefined) {
		return children;
	}
	return (
		<div className={classes["root"]}>
			{children}
			<div className={classes["info"]}>
				<FieldInfo label={label} text={text} testId={testId} />
			</div>
		</div>
	);
}
