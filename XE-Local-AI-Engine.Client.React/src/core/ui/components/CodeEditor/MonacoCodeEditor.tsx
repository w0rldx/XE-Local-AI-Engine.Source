import { useComputedColorScheme } from "@mantine/core";
import { useEffect, useRef } from "react";

import type { CodeEditorProps } from "@/core/ui/components/CodeEditor/CodeEditor.types";
import { monaco } from "@/core/ui/components/CodeEditor/MonacoRuntime";

/**
 * The Monaco-backed body of `CodeEditor`. Only ever mounted through `React.lazy` so `./MonacoRuntime` (the ~3 MB editor
 * chunk) is fetched on first use, never on app boot. Owns exactly one editor instance for its lifetime; prop changes
 * are applied in place (value only when it actually differs, so a controlled round-trip does not reset the caret).
 */
export default function MonacoCodeEditor({
	value,
	language = "plaintext",
	readOnly = false,
	onChange,
	height = 320,
	wordWrap = false,
	"aria-label": ariaLabel,
	"data-testid": testId,
}: CodeEditorProps) {
	const containerRef = useRef<HTMLDivElement>(null);
	const editorRef = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
	const onChangeRef = useRef(onChange);
	onChangeRef.current = onChange;
	const colorScheme = useComputedColorScheme("light");

	// The instance is created once; later prop changes are applied in place by the effects below.
	// biome-ignore lint/correctness/useExhaustiveDependencies: create-once, update-in-place lifecycle.
	useEffect(() => {
		const container = containerRef.current;
		if (container === null) {
			return;
		}
		const editor = monaco.editor.create(container, {
			value,
			language,
			readOnly,
			ariaLabel,
			automaticLayout: true,
			minimap: { enabled: false },
			scrollBeyondLastLine: false,
			renderLineHighlight: readOnly ? "none" : "line",
			wordWrap: wordWrap ? "on" : "off",
			fontSize: 13,
			domReadOnly: readOnly,
		});
		editorRef.current = editor;
		const subscription = editor.onDidChangeModelContent(() => onChangeRef.current?.(editor.getValue()));
		return () => {
			subscription.dispose();
			const model = editor.getModel();
			editor.dispose();
			// `create` allocated the model implicitly; the editor does not own it, so it is released here explicitly.
			model?.dispose();
			editorRef.current = null;
		};
	}, []);

	useEffect(() => {
		const editor = editorRef.current;
		if (editor && editor.getValue() !== value) {
			editor.setValue(value);
		}
	}, [value]);

	useEffect(() => {
		const model = editorRef.current?.getModel();
		if (model) {
			monaco.editor.setModelLanguage(model, language);
		}
	}, [language]);

	useEffect(() => {
		editorRef.current?.updateOptions({
			readOnly,
			domReadOnly: readOnly,
			renderLineHighlight: readOnly ? "none" : "line",
			wordWrap: wordWrap ? "on" : "off",
			ariaLabel,
		});
	}, [readOnly, wordWrap, ariaLabel]);

	useEffect(() => {
		monaco.editor.setTheme(colorScheme === "dark" ? "xe-dark" : "xe-light");
	}, [colorScheme]);

	return (
		<div
			ref={containerRef}
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
