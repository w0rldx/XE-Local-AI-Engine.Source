import { Button, Loader } from "@mantine/core";
import { IconEye, IconEyeOff } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import type { DevelopmentArtifact } from "@/features/development/models/DevelopmentModels";
import { useDevelopmentArtifactContent } from "@/features/development/queries/useDevelopment";

/** Every artifact kind other than the git patch is a JSON document (manifest, validation/review reports). */
function artifactLanguage(kind: string | undefined): string {
	if (kind === "Patch") {
		return "diff";
	}

	// A prompt is plain text, not a document. Falling through to the JSON arm rendered it unhighlighted and, worse,
	// as something an operator would read as malformed JSON.
	if (kind === "Prompt") {
		return "plaintext";
	}

	return "json";
}

/**
 * The raw body of one stored artifact in the shared code viewer — the patch as a unified diff, everything else as
 * JSON. This is the inspection path for what the engine produced: the same decrypted, blob-verified read the
 * validation view uses, shown verbatim instead of interpreted.
 */
export function ArtifactContentView({ artifact }: { readonly artifact: DevelopmentArtifact }) {
	const { t } = useTranslation();
	const contentQuery = useDevelopmentArtifactContent(artifact.projectId, artifact.taskId, artifact.id);

	if (contentQuery.isPending) {
		return <Loader size="sm" aria-label={t("pages.development.artifacts.loading", "Loading the artifact")} />;
	}
	if (contentQuery.error) {
		return (
			<InlineErrorAlert
				message={t("pages.development.artifacts.loadError", "Could not load the artifact.")}
				data-testid="development-artifact-load-error"
			/>
		);
	}
	return (
		<CodeEditor
			value={contentQuery.data?.content ?? ""}
			language={artifactLanguage(artifact.kind)}
			readOnly={true}
			height={360}
			aria-label={t("pages.development.artifacts.viewerLabel", "{{kind}} artifact", { kind: artifact.kind })}
			data-testid={`development-artifact-content-${artifact.id}`}
		/>
	);
}

/** "View" toggle for one artifact row; the selection lives in the panel so only one viewer is open at a time. */
export function ArtifactViewButton({
	artifact,
	open,
	onToggle,
}: {
	readonly artifact: DevelopmentArtifact;
	readonly open: boolean;
	readonly onToggle: (artifact: DevelopmentArtifact) => void;
}) {
	const { t } = useTranslation();
	return (
		<Button
			size="compact-xs"
			variant="subtle"
			leftSection={open ? <IconEyeOff size={14} /> : <IconEye size={14} />}
			onClick={() => onToggle(artifact)}
			data-testid={`development-artifact-view-${artifact.id}`}
		>
			{open ? t("pages.development.artifacts.hide", "Hide") : t("pages.development.artifacts.view", "View")}
		</Button>
	);
}
