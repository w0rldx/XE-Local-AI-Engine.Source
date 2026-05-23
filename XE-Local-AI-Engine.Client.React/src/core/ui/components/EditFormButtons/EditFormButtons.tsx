import { Button } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { EditFormButtonsProperties } from "@/core/ui/components/EditFormButtons/EditFormButtons.types";

export function EditFormButtons({ isSubmitting, allowEdit, handleEditClick, handleSubmit }: EditFormButtonsProperties) {
	const { t } = useTranslation();

	return (
		<div>
			{(!allowEdit && (
				<Button variant="filled" color="primary" onClick={handleEditClick}>
					{t("pages.userSettings.editProfile")}
				</Button>
			)) || (
				<div className="flex gap-5">
					<Button variant="filled" color="primary" type="button" disabled={isSubmitting} onClick={handleSubmit}>
						{t("pages.userSettings.saveEdit")}
					</Button>
					<Button variant="filled" color="secondary" type="button" onClick={handleEditClick}>
						{t("pages.userSettings.cancelEdit")}
					</Button>
				</div>
			)}
		</div>
	);
}
