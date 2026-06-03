import { Box, SegmentedControl, Stack, Textarea } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

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
	"data-testid"?: string;
}

/**
 * Mantine-native markdown editor: a capped autosize Textarea plus a
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

			{mode === "edit" ? (
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
