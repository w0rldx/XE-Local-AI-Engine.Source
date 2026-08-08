import {
	validationProblemProbe,
	type XeLocalAiEngineClientEndpointsApiFoundationV1ValidationProblemProbeResponse,
} from "@/core/api/generated";

export async function probeLocalApi(
	name: string,
): Promise<XeLocalAiEngineClientEndpointsApiFoundationV1ValidationProblemProbeResponse> {
	const { data } = await validationProblemProbe({
		body: { name },
		throwOnError: true,
	});

	return data;
}
