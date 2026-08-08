import "./NotFound.css";

import { Button, Text, Title } from "@mantine/core";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

export function NotFound() {
	const { t } = useTranslation();
	return (
		<div className="root">
			<Title order={1} className="text-7xl">
				404
			</Title>
			<Title order={2} className="title">
				{t("pages.notFound.subtitleSecret")}
			</Title>
			<Text className="description">{t("pages.notFound.subtitleNotFound")}</Text>
			<div>
				<Link to="/">
					<Button variant="filled">{t("pages.notFound.backToHomeText")}</Button>
				</Link>
			</div>
		</div>
	);
}
