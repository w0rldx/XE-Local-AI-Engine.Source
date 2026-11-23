import { Stack, Text } from "@mantine/core";

export function ModelInspectMetadataItem({ label, value }: { label: string; value: string }) {
	return (
		<Stack gap={0}>
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			{/* Long dotted names (tensor names, paths) have no break point and would widen the page on a phone. */}
			<Text size="sm" fw={500} style={{ overflowWrap: "anywhere" }}>
				{value}
			</Text>
		</Stack>
	);
}
