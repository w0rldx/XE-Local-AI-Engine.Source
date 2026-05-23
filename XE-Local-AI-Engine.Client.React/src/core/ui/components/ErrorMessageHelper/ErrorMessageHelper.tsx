import { Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { IErrorMessageHelperProperties } from "@/core/ui/components/ErrorMessageHelper/ErrorMessageHelper.types";
import { getErrorMessageText } from "@/core/ui/utils/ErrorMessages";

export function ErrorMessageHelper({ text }: IErrorMessageHelperProperties) {
	const { t } = useTranslation();
	const resolvedText = getErrorMessageText(text);

	if (!resolvedText) {
		return <Text c="red">{t("errorMessages.defaultErrorMessage")}</Text>;
	}

	const [errorTranslationKey, minValueRaw] = resolvedText.split(":");

	if (!errorTranslationKey) {
		return <Text c="red">{t("errorMessages.defaultErrorMessage")}</Text>;
	}

	if (minValueRaw) {
		const minValue = Number.parseInt(minValueRaw, 10);

		if (!Number.isNaN(minValue)) {
			return <Text c="red">{t(errorTranslationKey, { min: minValue })}</Text>;
		}
	}

	return <Text c="red">{t(errorTranslationKey)}</Text>;
}
