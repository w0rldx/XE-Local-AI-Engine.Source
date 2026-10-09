import { Alert, Skeleton } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { lazy, Suspense } from "react";
import { ErrorBoundary } from "react-error-boundary";
import { useTranslation } from "react-i18next";

import type { CodeDiffViewerProps } from "@/core/ui/components/CodeEditor/CodeDiffViewer.types";

const MonacoCodeDiffViewer = lazy(() => import("@/core/ui/components/CodeEditor/MonacoCodeDiffViewer"));

/**
 * A read-only side-by-side (inline on narrow screens) comparison of two texts, on the same lazily-loaded Monaco chunk as
 * `CodeEditor`, so app boot pays nothing for it.
 *
 * If the chunk cannot load, an alert replaces the comparison: two plain blocks of text would not show what changed,
 * and every caller keeps the texts themselves reachable elsewhere.
 */
export function CodeDiffViewer(props: CodeDiffViewerProps) {
	const { t } = useTranslation();
	const height = props.height ?? 320;
	return (
		<ErrorBoundary
			fallback={
				<Alert role="alert" color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid={props["data-testid"]}>
					{t("components.codeEditor.diffUnavailable", "The comparison could not load.")}
				</Alert>
			}
		>
			<Suspense fallback={<Skeleton height={height} radius="sm" />}>
				<MonacoCodeDiffViewer {...props} height={height} />
			</Suspense>
		</ErrorBoundary>
	);
}
