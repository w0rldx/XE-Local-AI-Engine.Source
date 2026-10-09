import { Alert, Code, Skeleton, Textarea } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { lazy, Suspense } from "react";
import { ErrorBoundary } from "react-error-boundary";
import { useTranslation } from "react-i18next";

import type { CodeEditorProps } from "@/core/ui/components/CodeEditor/CodeEditor.types";

const MonacoCodeEditor = lazy(() => import("@/core/ui/components/CodeEditor/MonacoCodeEditor"));

/**
 * The shared code/text viewer-editor: Monaco, loaded on first mount from its own chunk so app boot pays nothing for it.
 * Read-only by default-usage in Dev Mode today; `onChange` turns the same surface into an editor.
 *
 * Renders a Skeleton of the same height while the chunk loads, and degrades if the chunk cannot be loaded (offline
 * chunk miss, blocked worker): a viewer to a plain `<Code block>`, an editor (`onChange` set) to a plain Textarea, so
 * the content stays readable and editable either way.
 *
 * Except under `inspect`: there the plain fallback would show untrusted text with its zero-width and look-alike
 * characters invisible again, which is worse than showing nothing. It fails closed to an alert instead.
 */
export function CodeEditor(props: CodeEditorProps) {
	const { t } = useTranslation();
	const height = props.height ?? 320;
	return (
		<ErrorBoundary
			fallback={
				props.inspect ? (
					<Alert
						role="alert"
						color="red"
						variant="light"
						icon={<IconAlertTriangle size={16} />}
						data-testid={props["data-testid"]}
					>
						{t(
							"components.codeEditor.inspectUnavailable",
							"The inspection editor could not load, so this content cannot be reviewed here.",
						)}
					</Alert>
				) : props.onChange ? (
					<Textarea
						value={props.value}
						onChange={(event) => props.onChange?.(event.currentTarget.value)}
						readOnly={props.readOnly}
						autosize={true}
						minRows={6}
						maxRows={20}
						styles={{ input: { fontFamily: "var(--mantine-font-family-monospace)" } }}
						aria-label={props["aria-label"] ?? t("components.codeEditor.fallbackLabel")}
						data-testid={props["data-testid"]}
					/>
				) : (
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
				)
			}
		>
			<Suspense fallback={<Skeleton height={height} radius="sm" />}>
				<MonacoCodeEditor {...props} height={height} />
			</Suspense>
		</ErrorBoundary>
	);
}
