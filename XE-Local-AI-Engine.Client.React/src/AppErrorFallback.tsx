import { Alert, Button, Center, Paper, Stack, Text, Title } from "@mantine/core";
import { IconAlertCircle } from "@tabler/icons-react";

import type { AppErrorFallbackProps } from "@/AppErrorFallback.types";

export function AppErrorFallback({ error, onRetry }: AppErrorFallbackProps) {
	const errorMessage = error instanceof Error ? error.message : "Unknown error";

	return (
		<Center mih="100vh" p="md">
			<Paper withBorder={true} radius="md" p="xl" maw={560} w="100%">
				<Stack gap="md">
					<Alert icon={<IconAlertCircle size={18} />} color="red" title="Something went wrong" variant="light">
						The application hit an unexpected error while rendering this page.
					</Alert>
					<Stack gap={4}>
						<Title order={3}>Unable to load this view</Title>
						<Text c="dimmed">Try again to re-render the current route.</Text>
					</Stack>
					<Text ff="monospace" size="sm">
						{errorMessage}
					</Text>
					<Button onClick={onRetry}>Try again</Button>
				</Stack>
			</Paper>
		</Center>
	);
}
