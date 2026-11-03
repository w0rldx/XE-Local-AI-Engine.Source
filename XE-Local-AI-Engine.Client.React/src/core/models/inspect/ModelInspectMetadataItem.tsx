import { Stack, Text } from "@mantine/core";

export function ModelInspectMetadataItem({ label, value }: { label: string; value: string }) {
	return (
		<Stack gap={0}>
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			<Text size="sm" fw={500}>
				{value}
			</Text>
		</Stack>
	);
}
