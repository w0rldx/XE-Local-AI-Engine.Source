import { Box, Input, SegmentedControl, Stack, Textarea } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";

import { MarkdownView } from "@/core/ui/components/MarkdownView/MarkdownView";

type EditMode = "edit" | "preview";

interface MarkdownEditorFieldProps {
	value: string;
	onChange: (value: string) => void;
	label?: string;
	description?: string;
	error?: string;
	required?: boolean;
	placeholder?: string;
	/** Minimum visible rows in edit mode (default 4). */
	minRows?: number;
	/** Maximum rows before the textarea scrolls (default 12). */
	maxRows?: number;
	/**
	 * Edit surface: a capped autosize Textarea (default) or the shared Monaco `CodeEditor` with markdown highlighting,
	 * lazily loaded. The code editor has no placeholder and a fixed height; `placeholder`, `minRows` and `maxRows`
	 * only shape the textarea (`minRows` still sizes the preview).
	 */
	editor?: "textarea" | "code";
	"data-testid"?: string;
}

/**
 * Mantine-native markdown editor: a capped autosize Textarea or a Monaco code editor, plus a
 * SegmentedControl that toggles between Edit and Preview (MarkdownView).
 * Labels are i18n-keyed; t() default-arg fallback covers the period before
 * worker-1 lands the keys in en/de.json.
 */
export function MarkdownEditorField({
	value,
	onChange,
	label,
	description,
	error,
	required,
	placeholder,
	minRows = 4,
	maxRows = 12,
	editor = "textarea",
	"data-testid": testId,
}: MarkdownEditorFieldProps) {
	const { t } = useTranslation();
	const [mode, setMode] = useState<EditMode>("edit");

	const editLabel = t("components.markdownEditor.edit", "Edit");
	const previewLabel = t("components.markdownEditor.preview", "Preview");

	return (
		<Stack gap="xs" data-testid={testId}>
			<Box style={{ display: "flex", justifyContent: "flex-end" }}>
				<SegmentedControl
					size="xs"
					value={mode}
					onChange={(v) => setMode(v as EditMode)}
					data={[
						{ label: editLabel, value: "edit" },
						{ label: previewLabel, value: "preview" },
					]}
				/>
			</Box>

			{mode === "edit" && editor === "code" ? (
				// Monaco renders a div, not a labelable control, so the wrapper's <label> cannot point at it: the editor
				// carries the label as its own accessible name instead.
				<Input.Wrapper label={label} description={description} error={error} required={required}>
					<CodeEditor
						value={value}
						onChange={onChange}
						language="markdown"
						wordWrap={true}
						aria-label={label}
						data-testid={testId ? `${testId}-editor` : undefined}
					/>
				</Input.Wrapper>
			) : mode === "edit" ? (
				<Textarea
					value={value}
					onChange={(e) => onChange(e.currentTarget.value)}
					label={label}
					description={description}
					error={error}
					required={required}
					placeholder={placeholder}
					autosize={true}
					minRows={minRows}
					maxRows={maxRows}
					data-testid={testId ? `${testId}-textarea` : undefined}
				/>
			) : (
				<Box data-testid={testId ? `${testId}-preview` : undefined} style={{ minHeight: `${minRows * 1.5}rem` }}>
					{label && (
						<Box
							component="label"
							style={{
								display: "block",
								fontWeight: 500,
								fontSize: "var(--mantine-font-size-sm)",
								marginBottom: 4,
							}}
						>
							{label}
							{required && (
								<Box component="span" style={{ color: "var(--mantine-color-red-6)", marginLeft: 2 }}>
									*
								</Box>
							)}
						</Box>
					)}
					<MarkdownView content={value} />
					{description && (
						<Box
							style={{
								fontSize: "var(--mantine-font-size-xs)",
								color: "var(--mantine-color-dimmed)",
								marginTop: 4,
							}}
						>
							{description}
						</Box>
					)}
					{error && (
						<Box
							style={{
								fontSize: "var(--mantine-font-size-xs)",
								color: "var(--mantine-color-red-6)",
								marginTop: 4,
							}}
						>
							{error}
						</Box>
					)}
				</Box>
			)}
		</Stack>
	);
}
