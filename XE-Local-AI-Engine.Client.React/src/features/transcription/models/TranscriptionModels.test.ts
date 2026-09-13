import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import {
	toTranscriptChannel,
	toTranscriptionSessionDetailView,
	toTranscriptionSessionStatus,
	toTranscriptionSourceKind,
	unsupportedContainerDetail,
} from "@/features/transcription/models/TranscriptionModels";

function segment(seq: number, overrides: Record<string, unknown> = {}) {
	return {
		id: `segment-${seq}`,
		seq,
		startMs: seq * 1000,
		endMs: seq * 1000 + 500,
		text: `line ${seq}`,
		channel: "Mono",
		...overrides,
	};
}

function detail(segments: ReturnType<typeof segment>[]) {
	return {
		session: {
			id: "session-1",
			title: null,
			status: "Completed",
			sourceKind: "File",
			modelId: "base",
			segmentCount: segments.length,
			createdAtUtc: 1,
			updatedAtUtc: 2,
		},
		segments,
		config: { languageMode: "auto", translate: false, maxWindowSeconds: 5, channelAttribution: false },
	};
}

describe("transcription wire normalizers", () => {
	it("keeps a known status and falls back to Created for an unknown one", () => {
		expect(toTranscriptionSessionStatus("Transcribing")).toBe("Transcribing");
		expect(toTranscriptionSessionStatus("Quantumised")).toBe("Created");
		expect(toTranscriptionSessionStatus(null)).toBe("Created");
	});

	// Dictation / ApplicationProcess are persistable source kinds no surface can create yet, but they keep their own
	// identity: reporting a dictated session as a file would be a lie the operator cannot see through. Only a kind
	// outside the backend enum entirely falls back.
	it("keeps every backend source kind and falls back only for an unknown one", () => {
		expect(toTranscriptionSourceKind("MicrophoneAndSystem")).toBe("MicrophoneAndSystem");
		expect(toTranscriptionSourceKind("Dictation")).toBe("Dictation");
		expect(toTranscriptionSourceKind("ApplicationProcess")).toBe("ApplicationProcess");
		expect(toTranscriptionSourceKind("Telepathy")).toBe("File");
	});

	// An unknown channel renders as Mono, i.e. with NO badge — a badge naming a channel the UI cannot explain is
	// worse than no badge at all.
	it("degrades an unknown channel to Mono so no badge is invented", () => {
		expect(toTranscriptChannel("Others")).toBe("Others");
		expect(toTranscriptChannel("Speaker3")).toBe("Mono");
	});
});

describe("toTranscriptionSessionDetailView", () => {
	it("orders segments by seq regardless of the order they arrived in", () => {
		const view = toTranscriptionSessionDetailView(detail([segment(3), segment(1), segment(2)]));

		expect(view.segments.map((item) => item.seq)).toEqual([1, 2, 3]);
	});

	it("maps absent optional fields to null rather than undefined", () => {
		const view = toTranscriptionSessionDetailView(detail([segment(1)]));

		expect(view.session.title).toBeNull();
		expect(view.errorCode).toBeNull();
		expect(view.segments[0]?.confidence).toBeNull();
	});
});

describe("unsupportedContainerDetail", () => {
	// The upload's 415 is a typed domain body, not ProblemDetails: the interceptor still produces an ApiError but
	// parks the whole body on apiProblemDetails, which is where the readable-container list lives.
	it("reads the container list off a 415 ApiError", () => {
		const error = new ApiError(415, {
			reason: "unsupported-container",
			message: "That container cannot be read.",
			detectedContainer: "Matroska",
			supportedContainers: ["Wav", "Mp3"],
			ffmpegRequired: true,
		} as unknown as ProblemDetails);

		expect(unsupportedContainerDetail(error)).toEqual({ supportedContainers: ["Wav", "Mp3"], ffmpegRequired: true });
	});

	it("returns null for any other failure so the caller falls back to the message", () => {
		const problem: ProblemDetails = { type: "about:blank", title: "Bad Request", status: 400, detail: "no file" };

		expect(unsupportedContainerDetail(new ApiError(400, problem))).toBeNull();
		expect(unsupportedContainerDetail(new Error("network"))).toBeNull();
	});
});
