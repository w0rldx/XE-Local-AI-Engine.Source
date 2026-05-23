import { Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

export function Home() {
	const { t } = useTranslation();

	return (
		<div className="h-full w-full flex flex-col items-center justify-center">
			<div className="w-full flex flex-col items-center justify-center">
				<Title order={1}>{t("pages.home.title.welcome")}</Title>
			</div>
		</div>
	);
}
