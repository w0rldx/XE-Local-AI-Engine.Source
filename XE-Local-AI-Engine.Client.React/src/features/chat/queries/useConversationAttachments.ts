import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { t } from "i18next";
import { useCallback, useMemo, useState } from "react";

import { deleteConversationFile, listConversationFiles } from "@/core/api/generated";
import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { ApiError } from "@/core/api/errors/ApiError";
import { toast } from "@/core/ui/notifications/Toast";
import { toChatAttachment } from "@/features/chat/models/ChatAttachmentModels";
import type { ChatAttachment, PendingAttachmentUpload } from "@/features/chat/models/ChatAttachmentModels";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";

// Mirrors the server's default Security.MaxUploadFileSizeMb (25). Advisory only — the endpoint is the source of
// truth and re-enforces the cap; the client check just avoids a guaranteed-reject round trip and gives instant feedback.
const MAX_UPLOAD_SIZE_MB = 25;
const MAX_UPLOAD_SIZE_BYTES = MAX_UPLOAD_SIZE_MB * 1024 * 1024;

export interface UseConversationAttachmentsResult {
	readonly attachments: ChatAttachment[];
	// All current (non-deleted) attachment file ids for the conversation, re-sent on every turn (the send contract).
	readonly attachmentFileIds: string[];
	readonly pendingUploads: PendingAttachmentUpload[];
	readonly isLoading: boolean;
	readonly isUploading: boolean;
	uploadFiles(files: readonly File[]): void;
	removeAttachment(fileId: string): void;
}

function uploadErrorMessage(error: unknown, fallback: string): string {
	if (error instanceof ApiError) {
		const problem = error.apiProblemDetails as unknown as Record<string, unknown> | undefined;
		const detail = problem?.["detail"];
		if (typeof detail === "string" && detail.trim().length > 0) {
			return detail;
		}
		// FastEndpoints validation failures surface the specific message under an `errors` map rather than `detail`.
		const errors = problem?.["errors"];
		if (errors && typeof errors === "object") {
			for (const value of Object.values(errors as Record<string, unknown>)) {
				if (Array.isArray(value) && typeof value[0] === "string") {
					return value[0];
				}
				if (typeof value === "string") {
					return value;
				}
			}
		}
		const title = problem?.["title"];
		if (typeof title === "string" && title.trim().length > 0) {
			return title;
		}
	}
	if (error instanceof Error && error.message.trim().length > 0) {
		return error.message;
	}
	return fallback;
}

interface UseConversationAttachmentsOptions {
	// The active conversation id, or "" before one exists. The file list is keyed on this so switching threads
	// loads that thread's attachments and a brand-new conversation starts empty.
	conversationId: string;
	// Resolves the conversation id to upload into, creating + selecting one when none exists yet (cold start).
	// Returning "" signals the conversation could not be resolved, and the upload is aborted with an error toast.
	ensureConversationId: () => Promise<string>;
}

/**
 * Owns the per-conversation attachment surface: a TanStack Query list of uploaded files plus upload/delete
 * mutations. Uploads use the generated `uploadConversationFile` SDK call (multipart/form-data: the body's binary
 * `file` part is serialized to FormData by the generated client) with an axios upload-progress callback. The list is
 * invalidated on every upload/delete so the chip row reflects authoritative server state.
 */
export function useConversationAttachments({
	conversationId,
	ensureConversationId,
}: UseConversationAttachmentsOptions): UseConversationAttachmentsResult {
	const queryClient = useQueryClient();
	const [pendingUploads, setPendingUploads] = useState<PendingAttachmentUpload[]>([]);

	const { data: attachments = [], isLoading } = useQuery({
		queryKey: nodeChatQueryKeys.conversationFiles(conversationId),
		queryFn: async ({ signal }) => {
			const { data } = await listConversationFiles({ path: { conversationId }, signal, throwOnError: true });
			return (data.items ?? []).map(toChatAttachment);
		},
		// No conversation yet → nothing to load (a fresh thread starts empty).
		enabled: conversationId.length > 0,
	});

	const removeMutation = useMutation({
		mutationFn: async (fileId: string) => {
			await deleteConversationFile({ path: { conversationId, fileId }, throwOnError: true });
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationFiles(conversationId) });
		},
		onError: (error: unknown) => {
			toast.error(uploadErrorMessage(error, t("pages.chat.composer.attachments.removeFailed", "Failed to remove the attachment.")));
		},
	});

	const updatePendingPercent = useCallback((tempId: string, percent: number): void => {
		setPendingUploads((current) => current.map((upload) => (upload.tempId === tempId ? { ...upload, percent } : upload)));
	}, []);

	const removePending = useCallback((tempId: string): void => {
		setPendingUploads((current) => current.filter((upload) => upload.tempId !== tempId));
	}, []);

	const uploadOne = useCallback(
		async (targetConversationId: string, file: File): Promise<void> => {
			const tempId = crypto.randomUUID();
			setPendingUploads((current) => [...current, { tempId, name: file.name, sizeBytes: file.size, percent: 0 }]);

			try {
				const formData = new FormData();
				formData.append("file", file);
				// Posted through the shared axiosInstance (not the generated SDK) on purpose: the upload is
				// multipart/form-data, but the axios instance defaults Content-Type to application/json and hey-api's
				// binary body handling does not reliably override that — the file would serialize to `{}` as JSON.
				// Setting the multipart content type per-request lets axios append the boundary; auth + XSRF still ride
				// the instance interceptors, and the ProblemDetails interceptor turns a non-2xx into an ApiError. The
				// server is the authoritative validator (size cap, extension allow-list, extraction).
				await axiosInstance.post(buildLocalApiUrl(`chat/conversations/${targetConversationId}/uploads`), formData, {
					headers: { "Content-Type": "multipart/form-data" },
					onUploadProgress: (event) => {
						if (event.total) {
							updatePendingPercent(tempId, (event.loaded / event.total) * 100);
						}
					},
				});
				await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationFiles(targetConversationId) });
			} catch (error) {
				toast.error(
					uploadErrorMessage(
						error,
						t("pages.chat.composer.attachments.uploadFailed", "Failed to upload {{name}}.", { name: file.name }),
					),
				);
			} finally {
				removePending(tempId);
			}
		},
		[queryClient, removePending, updatePendingPercent],
	);

	const runUploads = useCallback(
		async (accepted: File[]): Promise<void> => {
			const targetConversationId = await ensureConversationId();
			if (targetConversationId.length === 0) {
				toast.error(t("pages.chat.composer.attachments.noConversation", "Could not prepare a conversation for the upload."));
				return;
			}
			// Independent uploads run concurrently; each settles its own optimistic chip + invalidation.
			await Promise.all(accepted.map((file) => uploadOne(targetConversationId, file)));
		},
		[ensureConversationId, uploadOne],
	);

	const uploadFiles = useCallback(
		(files: readonly File[]): void => {
			if (files.length === 0) {
				return;
			}

			// Client-side size guard (advisory): reject oversize files up front, upload the rest. The server re-checks.
			const accepted = files.filter((file) => {
				if (file.size > MAX_UPLOAD_SIZE_BYTES) {
					toast.error(
						t("pages.chat.composer.attachments.tooLarge", "{{name}} exceeds the {{limit}} MB upload limit.", {
							name: file.name,
							limit: MAX_UPLOAD_SIZE_MB,
						}),
					);
					return false;
				}
				return true;
			});
			if (accepted.length === 0) {
				return;
			}

			runUploads(accepted).catch((error: unknown) => {
				toast.error(uploadErrorMessage(error, t("pages.chat.composer.attachments.uploadFailed", "Failed to upload files.")));
			});
		},
		[runUploads],
	);

	const removeAttachment = useCallback(
		(fileId: string): void => {
			removeMutation.mutate(fileId);
		},
		[removeMutation],
	);

	const attachmentFileIds = useMemo(() => attachments.map((attachment) => attachment.fileId), [attachments]);

	return {
		attachments,
		attachmentFileIds,
		pendingUploads,
		isLoading,
		isUploading: pendingUploads.length > 0,
		uploadFiles,
		removeAttachment,
	};
}
