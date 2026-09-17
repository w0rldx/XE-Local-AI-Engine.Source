import { Code, Skeleton } from "@mantine/core";
import { lazy, Suspense } from "react";
import { ErrorBoundary } from "react-error-boundary";
import { useTranslation } from "react-i18next";

import type { CodeEditorProps } from "@/core/ui/components/CodeEditor/CodeEditor.types";

const MonacoCodeEditor = lazy(() => import("@/core/ui/components/CodeEditor/MonacoCodeEditor"));

/**
 * The shared code/text viewer-editor: Monaco, loaded on first mount from its own chunk so app boot pays nothing for it.
 * Read-only by default-usage in Dev Mode today; `onChange` turns the same surface into an editor.
 *
 * Renders a Skeleton of the same height while the chunk loads, and degrades to a plain `<Code block>` if the chunk
 * cannot be loaded (offline chunk miss, blocked worker) — the content stays readable either way.
 */
export function CodeEditor(props: CodeEditorProps) {
	const { t } = useTranslation();
	const height = props.height ?? 320;
	return (
		<ErrorBoundary
			fallback={
				// The fallback clips its content to `height` and scrolls, which a keyboard-only user could not reach
				// (Monaco provides its own focusable surface; this plain <Code> had none). `tabIndex={0}` makes the
				// region focusable and scrollable by arrow key, and a focusable region needs a name of its own.
				<Code
					block={true}
					tabIndex={0}
					aria-label={props["aria-label"] ?? t("components.codeEditor.fallbackLabel")}
					data-testid={props["data-testid"]}
					style={{ maxHeight: height, overflow: "auto" }}
				>
					{props.value}
				</Code>
			}
		>
			<Suspense fallback={<Skeleton height={height} radius="sm" />}>
				<MonacoCodeEditor {...props} height={height} />
			</Suspense>
		</ErrorBoundary>
	);
}
