import { useQueryClient } from "@tanstack/react-query";
import { t } from "i18next";
import { useCallback, useState } from "react";

import type { UploadKnowledgeDocumentResponse } from "@/core/api/generated";
import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { toast } from "@/core/ui/notifications/Toast";
import {
	isAcceptedKnowledgeFile,
	KNOWLEDGE_MAX_UPLOAD_SIZE_BYTES,
	KNOWLEDGE_MAX_UPLOAD_SIZE_MB,
} from "@/features/knowledge/models/KnowledgeModels";
import { knowledgeErrorMessage } from "@/features/knowledge/queries/KnowledgeErrorMessage";
import { knowledgeInvalidationKey, knowledgeQueryIds } from "@/features/knowledge/queries/useKnowledgeDocuments";

export interface KnowledgePendingUpload {
	readonly tempId: string;
	readonly name: string;
	readonly sizeBytes: number;
	readonly percent: number;
}

export interface UseKnowledgeUploadResult {
	readonly pendingUploads: readonly KnowledgePendingUpload[];
	readonly isUploading: boolean;
	uploadFiles(files: readonly File[]): void;
}

// Owns document ingestion: a multipart POST per file with an axios upload-progress callback, then a list
// invalidation so the freshly-queued row appears. Uploads go through the shared axiosInstance (NOT the generated
// SDK) on purpose — the request is multipart/form-data, but the axios instance defaults Content-Type to
// application/json and hey-api's binary body handling does not reliably override that (the file would serialize to
// `{}` as JSON). Setting the multipart content type per-request lets axios append the boundary; auth + XSRF still
// ride the instance interceptors, and the ProblemDetails interceptor turns a non-2xx into an ApiError. The server
// is the authoritative validator (size cap, extension allow-list, extraction, dedup).
export function useKnowledgeUpload(): UseKnowledgeUploadResult {
	const queryClient = useQueryClient();
	const [pendingUploads, setPendingUploads] = useState<readonly KnowledgePendingUpload[]>([]);

	const updatePendingPercent = useCallback((tempId: string, percent: number): void => {
		setPendingUploads((current) => current.map((upload) => (upload.tempId === tempId ? { ...upload, percent } : upload)));
	}, []);

	const removePending = useCallback((tempId: string): void => {
		setPendingUploads((current) => current.filter((upload) => upload.tempId !== tempId));
	}, []);

	const uploadOne = useCallback(
		async (file: File): Promise<void> => {
			const tempId = crypto.randomUUID();
			setPendingUploads((current) => [...current, { tempId, name: file.name, sizeBytes: file.size, percent: 0 }]);

			try {
				const formData = new FormData();
				formData.append("file", file);
				const { data } = await axiosInstance.post<UploadKnowledgeDocumentResponse>(
					buildLocalApiUrl("knowledge-base/documents"),
					formData,
					{
						headers: { "Content-Type": "multipart/form-data" },
						onUploadProgress: (event) => {
							if (event.total) {
								updatePendingPercent(tempId, (event.loaded / event.total) * 100);
							}
						},
					},
				);
				await queryClient.invalidateQueries({ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.listDocuments) });
				// A deduplicated upload matched an already-indexed document by content hash — no new work was queued.
				if (data.deduplicated) {
					toast.info(
						t("pages.knowledgeBase.upload.deduplicated", "{{name}} is already in the knowledge base.", { name: file.name }),
					);
				} else {
					toast.success(t("pages.knowledgeBase.upload.queued", "{{name}} is being indexed.", { name: file.name }));
				}
			} catch (error) {
				toast.error(
					knowledgeErrorMessage(
						error,
						t("pages.knowledgeBase.upload.failed", "Failed to upload {{name}}.", { name: file.name }),
					),
				);
			} finally {
				removePending(tempId);
			}
		},
		[queryClient, removePending, updatePendingPercent],
	);

	const uploadFiles = useCallback(
		(files: readonly File[]): void => {
			if (files.length === 0) {
				return;
			}

			// Advisory client-side guards: reject oversize + unsupported files up front with a friendly toast, upload the
			// rest. The server re-checks every accepted file.
			const accepted = files.filter((file) => {
				if (file.size > KNOWLEDGE_MAX_UPLOAD_SIZE_BYTES) {
					toast.error(
						t("pages.knowledgeBase.upload.tooLarge", "{{name}} exceeds the {{limit}} MB upload limit.", {
							name: file.name,
							limit: KNOWLEDGE_MAX_UPLOAD_SIZE_MB,
						}),
					);
					return false;
				}
				if (!isAcceptedKnowledgeFile(file.name)) {
					toast.error(
						t("pages.knowledgeBase.upload.unsupported", "{{name}} is not a supported document type.", { name: file.name }),
					);
					return false;
				}
				return true;
			});
			if (accepted.length === 0) {
				return;
			}

			// Independent uploads run concurrently; each settles its own optimistic chip + invalidation.
			Promise.all(accepted.map((file) => uploadOne(file))).catch((error: unknown) => {
				toast.error(knowledgeErrorMessage(error, t("pages.knowledgeBase.upload.failed", "Failed to upload files.")));
			});
		},
		[uploadOne],
	);

	return {
		pendingUploads,
		isUploading: pendingUploads.length > 0,
		uploadFiles,
	};
}
