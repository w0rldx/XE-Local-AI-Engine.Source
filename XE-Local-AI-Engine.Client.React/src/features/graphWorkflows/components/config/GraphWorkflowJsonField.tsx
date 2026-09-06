// A labelled JSON document with its Zod message underneath. The three JSON-shaped members (`defaultInput`,
// `responseJsonSchema`, `argumentsJson`) are held as TEXT on the canvas, so this is a `CodeEditor`, not a parsed
// object editor — and the message is the only place a parse failure surfaces before save.

import { Stack, Text } from "@mantine/core";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";

export interface GraphWorkflowJsonFieldProps {
	readonly label: string;
	readonly value: string | null;
	readonly error?: string;
	readonly readOnly?: boolean;
	readonly onChange: (next: string) => void;
	readonly "data-testid": string;
}

export function GraphWorkflowJsonField({
	label,
	value,
	error,
	readOnly = false,
	onChange,
	"data-testid": testId,
}: GraphWorkflowJsonFieldProps) {
	return (
		<Stack gap={4}>
			<Text size="sm" fw={500}>
				{label}
			</Text>
			<CodeEditor
				value={value ?? ""}
				language="json"
				height={140}
				readOnly={readOnly}
				aria-label={label}
				onChange={onChange}
				data-testid={testId}
			/>
			{error ? (
				<Text size="xs" c="red" data-testid={`${testId}-error`}>
					{error}
				</Text>
			) : null}
		</Stack>
	);
}
