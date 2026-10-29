// The bar above the canvas while a designer proposal is on it, and the Undo offer after one was accepted. It sits with
// the canvas rather than in the Assistant tab because a reply never takes focus: the operator may be on the Inspector.

import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { IconArrowBackUp, IconCheck, IconRefresh, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { GraphWorkflowEditorState } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";
import { designerSettingsDiff, summarizeDesignerChanges } from "@/features/graphWorkflows/models/GraphWorkflowDesignerModels";

export interface GraphWorkflowDesignerReviewBarProps {
	readonly editor: GraphWorkflowEditorState;
	readonly onRegenerate: () => void;
}

export function GraphWorkflowDesignerReviewBar({ editor, onRegenerate }: GraphWorkflowDesignerReviewBarProps) {
	const { t } = useTranslation();
	const review = editor.review;

	if (editor.mode === "reviewing" && review !== undefined) {
		const summary = summarizeDesignerChanges(review.changes);
		const onOff = (value: boolean): string =>
			value
				? t("pages.graphWorkflows.designer.review.settingOn", "on")
				: t("pages.graphWorkflows.designer.review.settingOff", "off");
		const settingLines = designerSettingsDiff(editor.settings, review.canvas.settings).map((entry) =>
			entry.member === "kind"
				? t("pages.graphWorkflows.designer.review.settingKind", "Kind: {{from}} → {{to}}", {
						from: t(`pages.graphWorkflows.settings.kindOption.${entry.from}`, entry.from),
						to: t(`pages.graphWorkflows.settings.kindOption.${entry.to}`, entry.to),
					})
				: entry.member === "acceptsAttachments"
					? t("pages.graphWorkflows.designer.review.settingAcceptsAttachments", "Accepts attachments: {{from}} → {{to}}", {
							from: onOff(entry.from),
							to: onOff(entry.to),
						})
					: t("pages.graphWorkflows.designer.review.settingRerunConfirmation", "Rerun confirmation: {{from}} → {{to}}", {
							from: onOff(entry.from),
							to: onOff(entry.to),
						}),
		);
		return (
			<Alert
				color={review.valid ? "blue" : "orange"}
				variant="light"
				title={t("pages.graphWorkflows.designer.review.title", "Proposed change")}
				data-testid="gw-review-bar"
			>
				<Stack gap="xs">
					<Text size="sm" data-testid="gw-review-summary">
						{t(
							"pages.graphWorkflows.designer.review.summary",
							"{{added}} added, {{modified}} changed, {{removed}} removed. Removed elements are shown faded.",
							{ ...summary },
						)}
					</Text>
					{settingLines.length === 0 ? null : (
						<Stack gap={2} data-testid="gw-review-settings">
							<Text size="sm">{t("pages.graphWorkflows.designer.review.settings", "Workflow settings change:")}</Text>
							{settingLines.map((line) => (
								<Text key={line} size="sm">
									{line}
								</Text>
							))}
						</Stack>
					)}
					{review.valid ? null : (
						<Stack gap={2} data-testid="gw-review-invalid">
							<Text size="sm">
								{t(
									"pages.graphWorkflows.designer.review.invalid",
									"This proposal does not pass validation, so it cannot be accepted. Reject it and ask again.",
								)}
							</Text>
							{review.errors.map((error) => (
								<Text key={error} size="xs" c="dimmed">
									{error}
								</Text>
							))}
						</Stack>
					)}
					{editor.isReviewStale ? (
						<Text size="sm" data-testid="gw-review-stale">
							{t(
								"pages.graphWorkflows.designer.review.stale",
								"The canvas changed after this was proposed. Ask again to get a proposal for the current graph.",
							)}
						</Text>
					) : null}
					<Group gap="xs">
						{editor.isReviewStale ? (
							<Button size="xs" leftSection={<IconRefresh size={14} />} onClick={onRegenerate} data-testid="gw-review-regenerate">
								{t("pages.graphWorkflows.designer.review.regenerate", "Regenerate")}
							</Button>
						) : (
							<Button
								size="xs"
								leftSection={<IconCheck size={14} />}
								disabled={!review.valid}
								onClick={() => editor.acceptProposal()}
								data-testid="gw-review-accept"
							>
								{t("pages.graphWorkflows.designer.review.accept", "Accept")}
							</Button>
						)}
						<Button
							size="xs"
							variant="default"
							leftSection={<IconX size={14} />}
							onClick={editor.rejectProposal}
							data-testid="gw-review-reject"
						>
							{t("pages.graphWorkflows.designer.review.reject", "Reject")}
						</Button>
					</Group>
				</Stack>
			</Alert>
		);
	}

	if (editor.canUndoAccept) {
		return (
			<Alert color="teal" variant="light" data-testid="gw-review-accepted">
				<Group justify="space-between" gap="xs">
					<Text size="sm">
						{t("pages.graphWorkflows.designer.review.accepted", "The proposal is on the canvas. Save to keep it.")}
					</Text>
					<Button
						size="xs"
						variant="default"
						leftSection={<IconArrowBackUp size={14} />}
						onClick={editor.undoAccept}
						data-testid="gw-review-undo"
					>
						{t("pages.graphWorkflows.designer.review.undo", "Undo")}
					</Button>
				</Group>
			</Alert>
		);
	}

	return null;
}
