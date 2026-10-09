import { useComputedColorScheme, useMantineTheme } from "@mantine/core";
import { useMediaQuery } from "@mantine/hooks";
import { useEffect, useRef } from "react";

import type { CodeDiffViewerProps } from "@/core/ui/components/CodeEditor/CodeDiffViewer.types";
import { monaco } from "@/core/ui/components/CodeEditor/MonacoRuntime";

interface DiffInstance {
	readonly editor: monaco.editor.IStandaloneDiffEditor;
	readonly original: monaco.editor.ITextModel;
	readonly modified: monaco.editor.ITextModel;
}

/**
 * The Monaco-backed body of `CodeDiffViewer`. Only ever mounted through `React.lazy` so `./MonacoRuntime` stays in the
 * lazily-fetched editor chunk. Owns one diff editor and its two models for its lifetime; text, language and layout
 * changes are applied in place.
 */
export default function MonacoCodeDiffViewer({
	original,
	modified,
	language = "plaintext",
	height = 320,
	"aria-label": ariaLabel,
	"data-testid": testId,
}: CodeDiffViewerProps) {
	const containerRef = useRef<HTMLDivElement>(null);
	const instanceRef = useRef<DiffInstance | null>(null);
	const colorScheme = useComputedColorScheme("light");
	const theme = useMantineTheme();
	// Read synchronously so a wide screen does not first build the inline layout and then flip.
	const sideBySide = useMediaQuery(`(min-width: ${theme.breakpoints.md})`, undefined, {
		getInitialValueInEffect: false,
	});

	// Created once; the effects below apply later prop changes in place.
	// biome-ignore lint/correctness/useExhaustiveDependencies: create-once, update-in-place lifecycle.
	useEffect(() => {
		const container = containerRef.current;
		if (container === null) {
			return;
		}
		const editor = monaco.editor.createDiffEditor(container, {
			readOnly: true,
			originalEditable: false,
			domReadOnly: true,
			// The breakpoint below decides the layout; Monaco's own width heuristic would override it.
			useInlineViewWhenSpaceIsLimited: false,
			renderSideBySide: sideBySide,
			// Monaco's default hides leading/trailing whitespace changes; in Markdown those are list nesting, indented code
			// and hard breaks, and Apply would accept them unseen.
			ignoreTrimWhitespace: false,
			wordWrap: "on",
			automaticLayout: true,
			minimap: { enabled: false },
			scrollBeyondLastLine: false,
			fontSize: 13,
		});
		const originalModel = monaco.editor.createModel(original, language);
		const modifiedModel = monaco.editor.createModel(modified, language);
		editor.setModel({ original: originalModel, modified: modifiedModel });
		instanceRef.current = { editor, original: originalModel, modified: modifiedModel };
		return () => {
			// The editor does not own models handed to `setModel`; both are released explicitly after it.
			editor.dispose();
			originalModel.dispose();
			modifiedModel.dispose();
			instanceRef.current = null;
		};
	}, []);

	useEffect(() => {
		const model = instanceRef.current?.original;
		if (model && model.getValue() !== original) {
			model.setValue(original);
		}
	}, [original]);

	useEffect(() => {
		const model = instanceRef.current?.modified;
		if (model && model.getValue() !== modified) {
			model.setValue(modified);
		}
	}, [modified]);

	useEffect(() => {
		const instance = instanceRef.current;
		if (instance) {
			monaco.editor.setModelLanguage(instance.original, language);
			monaco.editor.setModelLanguage(instance.modified, language);
		}
	}, [language]);

	useEffect(() => {
		instanceRef.current?.editor.updateOptions({ renderSideBySide: sideBySide });
	}, [sideBySide]);

	useEffect(() => {
		monaco.editor.setTheme(colorScheme === "dark" ? "xe-dark" : "xe-light");
	}, [colorScheme]);

	return (
		<div
			ref={containerRef}
			role="group"
			aria-label={ariaLabel}
			data-testid={testId}
			style={{
				height,
				minWidth: 0,
				border: "1px solid var(--mantine-color-default-border)",
				borderRadius: "var(--mantine-radius-sm)",
				overflow: "hidden",
			}}
		/>
	);
}
