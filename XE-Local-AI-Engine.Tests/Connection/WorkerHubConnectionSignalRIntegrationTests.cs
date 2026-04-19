namespace XE_Local_AI_Engine.Tests.Connection;

using System.Net.Http;
using System.Net.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Tests.Fixtures;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class WorkerHubConnectionSignalRIntegrationTests
{
    [Test]
    public async Task EncryptedRuntimePackageRoundTrip_WhenWorkerReceivesInvocationAndSendsChunkAndCompleted_PreservesPayloads()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var tokenStore = MockTokenStore.Paired("test-access-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30));
        var sender = new MockHubMessageSender();
        var deadLetterStore = Substitute.For<IDeadLetterStore>();
        deadLetterStore.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>([]));

        var deadLetterFlushService = new DeadLetterFlushService(
            deadLetterStore,
            new Lazy<IHubMessageSender>(() => sender),
            NullLogger<DeadLetterFlushService>.Instance);

        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await using var connection = new WorkerHubConnection(
            tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = fixture.HubBaseUri.ToString(),
                HubPath = fixture.HubPath,
            }),
            new ConnectionState(),
            new Lazy<ICapabilityReporter>(() => capabilityReporter),
            deadLetterFlushService,
            NullLogger<WorkerHubConnection>.Instance,
            CreateFixtureHttpOptionsConfigurator(fixture));

        var invocationAssigned = new TaskCompletionSource<EncryptedRuntimePackageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.InvocationAssignedReceived += (_, args) => invocationAssigned.TrySetResult(args.EncryptedRuntimePackage);

        await connection.ConnectAsync();

        var runtimePackage = new EncryptedRuntimePackageDto
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            EpochVersion = 7,
            NodeWrappedEpochKey = new byte[] { 1, 2, 3, 4 },
            ClientEphemeralPublicKey = new byte[] { 5, 6, 7, 8 },
            Ciphertext = new byte[] { 9, 10, 11, 12 },
            ContentIv = new byte[] { 13, 14, 15, 16 },
            Aad = new byte[] { 17, 18, 19, 20 },
            InvocationId = Guid.NewGuid(),
            ClientNodeId = Guid.NewGuid(),
        };

        await fixture.SendInvocationAssignedAsync(runtimePackage);

        var receivedPackage = await invocationAssigned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEncryptedRuntimePackageEqual(runtimePackage, receivedPackage);

        var chunkPayload = new EncryptedChunkEnvelopeV1
        {
            ConversationId = runtimePackage.ConversationId,
            MessageId = runtimePackage.MessageId,
            EpochVersion = runtimePackage.EpochVersion,
            ChunkIv = new byte[] { 21, 22, 23, 24 },
            ChunkCiphertext = new byte[] { 25, 26, 27, 28 },
            Sequence = 1,
        };

        var completedPayload = new EncryptedCompletedEnvelopeV1
        {
            ConversationId = runtimePackage.ConversationId,
            MessageId = runtimePackage.MessageId,
            EpochVersion = runtimePackage.EpochVersion,
            FinalIv = new byte[] { 31, 32, 33, 34 },
            FinalCiphertext = new byte[] { 35, 36, 37, 38 },
            TotalSequence = 2,
            TokenCounts = new Dictionary<string, long>
            {
                ["input"] = 11,
                ["output"] = 7,
            },
        };

        await connection.SendEncryptedChunkAsync(chunkPayload);
        await connection.SendEncryptedCompletedAsync(completedPayload);

        var receivedChunk = await fixture.WaitForFirstChunkAsync(TimeSpan.FromSeconds(5));
        var receivedCompleted = await fixture.WaitForCompletedAsync(TimeSpan.FromSeconds(5));

        AssertEncryptedChunkEqual(chunkPayload, receivedChunk);
        AssertEncryptedCompletedEqual(completedPayload, receivedCompleted);
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    private static Action<HttpConnectionOptions> CreateFixtureHttpOptionsConfigurator(FakeWorkerNodeFixture fixture)
    {
        return httpOptions =>
        {
            httpOptions.Transports = HttpTransportType.LongPolling;
            httpOptions.HttpMessageHandlerFactory = innerHandler => ConfigureFixtureCertificateValidation(innerHandler, fixture);
        };
    }

    private static HttpMessageHandler ConfigureFixtureCertificateValidation(HttpMessageHandler innerHandler, FakeWorkerNodeFixture fixture)
    {
        if (fixture.ServerCert is null)
        {
            throw new InvalidOperationException("The fake worker node fixture certificate is not available.");
        }

        if (innerHandler is not HttpClientHandler httpClientHandler)
        {
            return innerHandler;
        }

        var expectedThumbprint = fixture.ServerCert.Thumbprint;
        httpClientHandler.ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
            sslPolicyErrors is SslPolicyErrors.RemoteCertificateChainErrors
            && string.Equals(certificate?.GetCertHashString(), expectedThumbprint, StringComparison.OrdinalIgnoreCase);

        return httpClientHandler;
    }

    private static void AssertEncryptedRuntimePackageEqual(EncryptedRuntimePackageDto expected, EncryptedRuntimePackageDto actual)
    {
        AssertEx.Equal(expected.ConversationId, actual.ConversationId);
        AssertEx.Equal(expected.MessageId, actual.MessageId);
        AssertEx.Equal(expected.EpochVersion, actual.EpochVersion);
        AssertReadOnlyMemoryEqual(expected.NodeWrappedEpochKey, actual.NodeWrappedEpochKey);
        AssertReadOnlyMemoryEqual(expected.ClientEphemeralPublicKey, actual.ClientEphemeralPublicKey);
        AssertReadOnlyMemoryEqual(expected.Ciphertext, actual.Ciphertext);
        AssertReadOnlyMemoryEqual(expected.ContentIv, actual.ContentIv);
        AssertReadOnlyMemoryEqual(expected.Aad, actual.Aad);
        AssertEx.Equal(expected.InvocationId, actual.InvocationId);
        AssertEx.Equal(expected.ClientNodeId, actual.ClientNodeId);
    }

    private static void AssertEncryptedChunkEqual(EncryptedChunkEnvelopeV1 expected, EncryptedChunkEnvelopeV1 actual)
    {
        AssertEx.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        AssertEx.Equal(expected.ConversationId, actual.ConversationId);
        AssertEx.Equal(expected.MessageId, actual.MessageId);
        AssertEx.Equal(expected.EpochVersion, actual.EpochVersion);
        AssertReadOnlyMemoryEqual(expected.ChunkIv, actual.ChunkIv);
        AssertReadOnlyMemoryEqual(expected.ChunkCiphertext, actual.ChunkCiphertext);
        AssertEx.Equal(expected.Sequence, actual.Sequence);
    }

    private static void AssertEncryptedCompletedEqual(EncryptedCompletedEnvelopeV1 expected, EncryptedCompletedEnvelopeV1 actual)
    {
        AssertEx.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        AssertEx.Equal(expected.ConversationId, actual.ConversationId);
        AssertEx.Equal(expected.MessageId, actual.MessageId);
        AssertEx.Equal(expected.EpochVersion, actual.EpochVersion);
        AssertReadOnlyMemoryEqual(expected.FinalIv, actual.FinalIv);
        AssertReadOnlyMemoryEqual(expected.FinalCiphertext, actual.FinalCiphertext);
        AssertEx.Equal(expected.TotalSequence, actual.TotalSequence);
        AssertEx.Equal(expected.TokenCounts.Count, actual.TokenCounts.Count);

        foreach (var expectedTokenCount in expected.TokenCounts)
        {
            AssertEx.True(actual.TokenCounts.TryGetValue(expectedTokenCount.Key, out var actualValue));
            AssertEx.Equal(expectedTokenCount.Value, actualValue);
        }
    }

    private static void AssertReadOnlyMemoryEqual(ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> actual)
    {
        AssertEx.True(expected.Span.SequenceEqual(actual.Span));
    }
}
