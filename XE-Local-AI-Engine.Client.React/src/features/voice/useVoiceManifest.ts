import { useQuery } from "@tanstack/react-query";

import { getVoiceManifestOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { VoiceManifest } from "@/core/runtime/VoiceManifest";
import { adaptVoiceManifest } from "@/features/voice/VoiceManifestAdapter";

// Server-state for the voice manifest (the operator-owned gate + model/voice catalog). Reads through the generated
// hey-api `getVoiceManifestOptions()` (which wires the shared axios instance + TanStack Query AbortSignal) wrapped
// in withResponseValidation so a zod response-shape failure surfaces as an ApiError. The `select` adapter maps the
// all-optional generated DTO into the strict client-side `VoiceManifest`, so consumers never touch the raw wire shape.
// No mirroring into zustand — this is server-state only.

// Voice is dev-gated, and the manifest endpoint is Operator-gated, so the query is disabled unless the caller
// opts in (the provider passes developerMode). The manifest is near-static config: cache it for the session.
const VOICE_MANIFEST_STALE_TIME_MS = 5 * 60 * 1000;

export interface UseVoiceManifestOptions {
	readonly enabled?: boolean;
}

export function useVoiceManifest(options?: UseVoiceManifestOptions): {
	readonly manifest: VoiceManifest | undefined;
	readonly isLoading: boolean;
	readonly isError: boolean;
} {
	const query = useQuery({
		...withResponseValidation(getVoiceManifestOptions()),
		select: adaptVoiceManifest,
		enabled: options?.enabled ?? true,
		staleTime: VOICE_MANIFEST_STALE_TIME_MS,
		retry: false,
	});

	return { manifest: query.data, isLoading: query.isLoading, isError: query.isError };
}
