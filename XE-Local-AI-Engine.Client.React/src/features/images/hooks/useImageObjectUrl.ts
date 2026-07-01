import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

// Fetches a generated PNG for display. The retrieve endpoint is Operator-gated (needs the bearer token), and the
// generated `retrieveImage` op models the response as 204/void (the OpenAPI spec does not describe the binary body),
// so an <img src> can't carry the auth header and the generated op can't return bytes. Instead we fetch the PNG as a
// blob through the shared axios instance — whose request interceptor attaches `Authorization: Bearer <token>`
// automatically (src/core/api/axios/Interceptors.ts) — then object-URL it for the <img>. The blob is cached by
// TanStack Query (keyed on imageId); the object URL is created/revoked in an effect so it never leaks.

const IMAGE_BLOB_QUERY_KEY = "image-blob";

async function fetchImageBlob(imageId: string, signal: AbortSignal): Promise<Blob> {
	const response = await axiosInstance.get<Blob>(buildLocalApiUrl(`images/${imageId}`), {
		responseType: "blob",
		signal,
	});
	return response.data;
}

/** Returns a blob object URL for the decrypted PNG of `imageId`, or undefined until it resolves / when no id. */
export function useImageObjectUrl(imageId: string | null | undefined): { url: string | undefined; isLoading: boolean; isError: boolean } {
	const { data: blob, isLoading, isError } = useQuery({
		queryKey: [IMAGE_BLOB_QUERY_KEY, imageId],
		queryFn: ({ signal }) => fetchImageBlob(imageId as string, signal),
		enabled: Boolean(imageId),
		staleTime: Number.POSITIVE_INFINITY,
	});

	const [url, setUrl] = useState<string | undefined>(undefined);

	useEffect(() => {
		if (!blob) {
			setUrl(undefined);
			return;
		}
		const objectUrl = URL.createObjectURL(blob);
		setUrl(objectUrl);
		return () => {
			URL.revokeObjectURL(objectUrl);
		};
	}, [blob]);

	return { url, isLoading: Boolean(imageId) && isLoading, isError };
}
