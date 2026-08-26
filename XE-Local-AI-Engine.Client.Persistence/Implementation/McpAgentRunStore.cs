namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>SQLite-serialized durable ledger for inbound MCP runs.</summary>
public sealed partial class McpAgentRunStore : IMcpAgentRunStore
{
    public const int AccountingVersion = 1;
    public const int MaxTaskUtf8Bytes = 32 * 1024;
    public const int MaxInstructionsUtf8Bytes = 16 * 1024;
    public const int MaxResultCharacters = 24_000;
    public const int MaxDisplayUtf8Bytes = 2 * 1024;
    public const long PayloadRetentionMilliseconds = 24L * 60 * 60 * 1000;

    public const int TombstoneReservationBytesV1 = McpAgentRunPayloadProtector.FixedRecordOverheadBytes
                                                   + 16 // request id
                                                   + 32 // keyed request fingerprint
                                                   + 4 // accounting version
                                                   + 4 // terminal status
                                                   + 8 // version
                                                   + 128 // maximum safe ASCII failure code
                                                   + 8 // accepted timestamp
                                                   + 8 // terminal timestamp
                                                   + 8 // compaction timestamp
                                                   + 8; // persisted tombstone logical-byte charge

    public const int MaxNonterminalRuns = 32;
    public const long MaxIdentityCount = 1_000_000;
    public const long MaxActivePayloadBytes = 256L * 1024 * 1024;
    public const long MaxTombstoneLogicalBytes = 128L * 1024 * 1024;

    private readonly string _connectionString;
    private readonly McpAgentRunPayloadProtector _protector;

    public McpAgentRunStore(NodeChatDbContext dbContext, McpAgentRunPayloadProtector protector)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _connectionString = dbContext.Database.GetConnectionString()
                            ?? throw new InvalidOperationException("The MCP run store requires a configured SQLite connection string.");
    }
}
