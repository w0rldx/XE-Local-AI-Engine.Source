import { Alert, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { DirectInferenceMetrics } from "@/core/directInference/DirectInferenceMetrics";
import type { DirectInferenceResult } from "@/core/directInference/DirectInferenceModels";

const gpuWorkExclusiveCode = "GpuWorkExclusive";

export function DirectInferenceErrorAlert({ result }: { result: DirectInferenceResult }) {
	const { t } = useTranslation();
	const errorText =
		result.errorCode === gpuWorkExclusiveCode
			? t(
					"pages.directInference.gpuWorkExclusive",
					"A training run, evaluation or export holds the GPU. Try again when it has finished.",
				)
			: result.errorMessage;
	return errorText ? (
		<Alert color="red" title={t("pages.directInference.errorTitle", "The request failed")}>
			{errorText}
		</Alert>
	) : null;
}

export function DirectInferenceOutput({ result }: { result: DirectInferenceResult }) {
	const { t } = useTranslation();
	return (
		<>
			{result.reasoning.length > 0 && (
				<Text
					size="sm"
					c="dimmed"
					style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}
					data-testid="direct-inference-reasoning"
				>
					{result.reasoning}
				</Text>
			)}
			<Text style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }} data-testid="direct-inference-output">
				{result.text}
			</Text>
			{result.finishReason === "length" && (
				<Alert color="yellow" data-testid="direct-inference-cut-off">
					{t(
						"pages.directInference.cutOff",
						"The answer was cut off, at the output limit or because the context is full. Raise Max output tokens for the first; shorten the prompt or load the model with a larger context for the second.",
					)}
				</Alert>
			)}
			<DirectInferenceMetrics result={result} />
		</>
	);
}
