import { Code, Group, Text } from "@mantine/core";

/**
 * Splits an engine-authored terminal reason into its stable code and its prose.
 *
 * The backend emits `[some_code] Sentence…` for failures it diagnosed itself, and a bare sentence
 * for everything else. Both render; only the first gets a code chip. Parsing rather than adding a
 * second wire field keeps this a display concern — an unrecognised shape degrades to plain prose.
 */
function splitTerminalReason(reason: string): { code: string | null; message: string } {
	const match = /^\[([a-z0-9_]+)]\s*(.*)$/s.exec(reason);
	if (match === null) {
		return { code: null, message: reason };
	}

	return { code: match[1] ?? null, message: match[2] ?? "" };
}

export function AttemptTerminalReason({ reason, color }: { reason: string; color: string }) {
	const { code, message } = splitTerminalReason(reason);

	return (
		<Group gap="xs" align="flex-start" wrap="nowrap">
			{code === null ? null : (
				<Code c={color} data-testid="development-attempt-reason-code">
					{code}
				</Code>
			)}
			<Text size="sm" c="dimmed">
				{message}
			</Text>
		</Group>
	);
}
