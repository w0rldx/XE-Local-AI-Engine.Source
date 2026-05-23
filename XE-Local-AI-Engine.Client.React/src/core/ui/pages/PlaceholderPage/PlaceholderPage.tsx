import { Container, Stack, Text, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

interface PlaceholderPageProperties {
	readonly titleKey: string;
	readonly titleFallback: string;
	readonly descriptionKey: string;
	readonly descriptionFallback: string;
}

export function PlaceholderPage({
	titleKey,
	titleFallback,
	descriptionKey,
	descriptionFallback,
}: PlaceholderPageProperties) {
	const { t } = useTranslation();

	return (
		<Container size="sm" py="xl">
			<Stack gap="md" align="center">
				<Title order={2}>{t(titleKey, titleFallback)}</Title>
				<Text c="dimmed" ta="center">
					{t(descriptionKey, descriptionFallback)}
				</Text>
			</Stack>
		</Container>
	);
}
