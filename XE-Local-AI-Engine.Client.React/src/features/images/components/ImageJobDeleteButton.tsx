import { Button, Group, Stack, Text, Tooltip } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";
import { type ImageJobView, isTerminalStatus } from "@/features/images/models/ImageModels";
import { useDeleteImageJob } from "@/features/images/queries/useImageQueries";

interface ImageJobDeleteButtonProps {
	job: ImageJobView;
}

/**
 * Deletes one finished job together with the image it produced. The node refuses a job it is still working on — with
 * 409 — so that is the one state the control is disabled in, and the tooltip names the way out (cancel it first).
 * The disabled button is a convenience gate, not the guarantee; the refusal lives on the node.
 *
 * The dialog stays open on a failure so the operator can retry after the list refreshes, and the confirmation says
 * plainly that the image goes with the job: this delete takes the PNG off disk, and nothing restores it.
 */
export function ImageJobDeleteButton({ job }: ImageJobDeleteButtonProps) {
	const { t } = useTranslation();
	const [confirmOpen, setConfirmOpen] = useState(false);
	const deleteJob = useDeleteImageJob();
	const blocked = !isTerminalStatus(job.status);

	return (
		<>
			<Tooltip
				label={
					blocked
						? t("pages.images.job.deleteBlocked", "This job is still running. Cancel it first, then delete it.")
						: t("pages.images.job.deleteHint", "Deletes the job and the image it produced.")
				}
				multiline={true}
				w={260}
			>
				<span>
					<Button
						size="xs"
						variant="subtle"
						color="red"
						leftSection={<IconTrash size={14} />}
						disabled={blocked}
						onClick={() => setConfirmOpen(true)}
						data-testid="image-job-delete"
					>
						{t("common.delete", "Delete")}
					</Button>
				</span>
			</Tooltip>

			<DialogShell
				opened={confirmOpen}
				onClose={() => setConfirmOpen(false)}
				title={t("pages.images.job.deleteTitle", "Delete this image job?")}
				size="md"
				data-testid="image-job-delete-confirm"
			>
				<Stack gap="md">
					<Text>
						{job.imageId === null
							? t(
									"pages.images.job.deleteConfirmNoImage",
									"This job and its prompt are deleted. It produced no image. This cannot be undone.",
								)
							: t(
									"pages.images.job.deleteConfirm",
									"This job, its prompt and the image it produced are deleted — the picture is removed from disk. This cannot be undone.",
								)}
					</Text>
					<Group justify="flex-end">
						<Button variant="default" onClick={() => setConfirmOpen(false)}>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button
							color="red"
							loading={deleteJob.isPending}
							onClick={() =>
								deleteJob.mutate(
									{ jobId: job.id, imageId: job.imageId },
									{
										onSuccess: () => {
											setConfirmOpen(false);
											toast.success(t("pages.images.job.deleted", "Image job deleted."));
										},
										onError: (error) =>
											toast.error(apiErrorMessage(error, t("pages.images.job.deleteError", "Could not delete this image job."))),
									},
								)
							}
							data-testid="image-job-delete-accept"
						>
							{t("common.delete", "Delete")}
						</Button>
					</Group>
				</Stack>
			</DialogShell>
		</>
	);
}
