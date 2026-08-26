import { Alert, Badge, Button, Code, Group, Stack, Text } from "@mantine/core";
import { IconCheck, IconGitPullRequest } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { DevelopmentPatchPreview } from "@/features/development/models/DevelopmentModels";

interface DevelopmentPatchApplyPanelProps {
	readonly apply: { readonly loading: boolean; readonly outcome?: string | null; readonly run: () => void };
	readonly preview: { readonly data?: DevelopmentPatchPreview; readonly loading: boolean; readonly run: () => void };
	readonly repositoryReady: boolean;
}

export function DevelopmentPatchApplyPanel({ apply, preview, repositoryReady }: DevelopmentPatchApplyPanelProps) {
	const { t } = useTranslation();
	return (
		<SectionCard
			actions={<Badge color="green">{t("pages.development.apply.awaiting", "Awaiting explicit approval")}</Badge>}
			data-testid="development-apply-panel"
			title={t("pages.development.apply.title", "Human-controlled patch apply")}
		>
			<Group>
				<Button
					data-testid="development-preview-patch"
					disabled={!repositoryReady}
					leftSection={<IconGitPullRequest size={16} />}
					loading={preview.loading}
					onClick={preview.run}
				>
					{t("pages.development.apply.preview", "Preview current patch")}
				</Button>
				<Button
					color="green"
					data-testid="development-apply-patch"
					disabled={!repositoryReady || !preview.data}
					leftSection={<IconCheck size={16} />}
					loading={apply.loading}
					onClick={apply.run}
				>
					{t("pages.development.apply.apply", "Apply verified patch")}
				</Button>
			</Group>
			{preview.data ? (
				<Stack>
					<Text size="sm">
						{t("pages.development.apply.subject", "Subject")} <Code>{preview.data.subjectHash}</Code> ·{" "}
						{t("pages.development.apply.patch", "patch")} <Code>{preview.data.patchHash}</Code> ·{" "}
						{t("pages.development.apply.manifest", "manifest")} <Code>{preview.data.manifestHash}</Code>
					</Text>
					<CodeEditor
						aria-label={t("pages.development.apply.previewLabel", "Verified patch preview")}
						data-testid="development-patch-preview"
						height={360}
						language="diff"
						readOnly={true}
						value={preview.data.patch ?? ""}
					/>
				</Stack>
			) : null}
			{apply.outcome ? <Alert color="green">{apply.outcome}</Alert> : null}
		</SectionCard>
	);
}
