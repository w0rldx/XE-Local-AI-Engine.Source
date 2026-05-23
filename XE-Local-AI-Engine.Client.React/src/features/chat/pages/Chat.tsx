import { Container, Stack, Text, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

export function Chat() {
	const { t } = useTranslation();

	return (
		<Container size="sm" py="xl">
			<Stack gap="md" align="center">
				<Title order={2}>{t("pages.chat.placeholder.title", "Local chat")}</Title>
				<Text c="dimmed" ta="center">
					{t(
						"pages.chat.placeholder.description",
						"Local chat is coming with the node adapter. This page is a placeholder until the local runtime and SignalR connection are wired up.",
					)}
				</Text>
			</Stack>
		</Container>
	);
}
