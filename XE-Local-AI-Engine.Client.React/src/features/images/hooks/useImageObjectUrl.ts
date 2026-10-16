import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { retrieveImage } from "@/core/api/generated";

// Fetches a generated PNG for display. The retrieve endpoint is Operator-gated (needs the bearer token), so an
// <img src> can't carry the auth header. The generated `retrieveImage` answers the bytes as a Blob, which is
// object-URLed for the <img>. The blob is cached by TanStack Query (keyed on imageId); the object URL is
// created/revoked in an effect so it never leaks.

const IMAGE_BLOB_QUERY_KEY = "image-blob";

/**
 * The cache key one decrypted PNG is held under. Exported so a delete can drop the blob it just removed from the
 * node: `staleTime: Infinity` means nothing else ever would, and the bytes would outlive the image for the session.
 */
export function imageBlobQueryKey(imageId: string): readonly [string, string] {
	return [IMAGE_BLOB_QUERY_KEY, imageId];
}

async function fetchImageBlob(imageId: string, signal: AbortSignal): Promise<Blob> {
	const { data } = await retrieveImage({ path: { imageId }, signal });
	return data;
}

/**
 * Returns a blob object URL for the decrypted PNG of `imageId`, or undefined until it resolves / when no id.
 *
 * The underlying `blob` is returned alongside the URL so a "save to disk" action can reuse the bytes already in memory
 * instead of re-fetching them; the object URL alone cannot be handed to an anchor download with a chosen file name.
 */
export function useImageObjectUrl(imageId: string | null | undefined): {
	url: string | undefined;
	blob: Blob | undefined;
	isLoading: boolean;
	isError: boolean;
} {
	const {
		data: blob,
		isLoading,
		isError,
	} = useQuery({
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

	return { url, blob, isLoading: Boolean(imageId) && isLoading, isError };
}
