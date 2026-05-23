import { ActionIcon, Text, Tooltip } from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { IDialogTextTitleBarProperties } from "@/core/ui/components/DialogTextTitleBar/DialogTextTitleBar.types";

export function DialogTextTitleBar({ title, handleClose, showCloseButton = true }: IDialogTextTitleBarProperties) {
	const { t } = useTranslation();

	return (
		<div>
			<div className="px-4 my-3 flex items-center justify-between">
				<Text size="xl">{title}</Text>
				<div className={showCloseButton ? "" : "hidden"}>
					<Tooltip label={t("components.dialogTextTitleBar.tooltip.close")}>
						<ActionIcon
							aria-label="close"
							onClick={handleClose}
							variant="subtle"
							color="gray"
							style={{ justifyContent: "flex-end" }}
						>
							<IconX />
						</ActionIcon>
					</Tooltip>
				</div>
			</div>
		</div>
	);
}
