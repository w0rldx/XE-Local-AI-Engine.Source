import {
	type XeLocalAiEngineClientEndpointsApiFoundationV1ValidationProblemProbeResponse,
	xeLocalAiEngineClientEndpointsApiFoundationV1ValidationProblemProbeEndpoint,
} from "@/core/api/generated";

export async function probeLocalApi(
	name: string,
): Promise<XeLocalAiEngineClientEndpointsApiFoundationV1ValidationProblemProbeResponse> {
	const { data } = await xeLocalAiEngineClientEndpointsApiFoundationV1ValidationProblemProbeEndpoint({
		body: { name },
		throwOnError: true,
	});

	return data;
}
