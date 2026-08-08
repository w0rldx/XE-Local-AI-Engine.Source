using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddKnowledgeVectorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "vector_dim",
                table: "knowledge_documents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "vector_identity",
                table: "knowledge_documents",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy:unversioned");

            migrationBuilder.AddColumn<string>(
                name: "vector_identity",
                table: "knowledge_chunk_vectors",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy:unversioned");

            // This is an explicit data migration, not an inference from the new column defaults. Every existing
            // projection predates the versioned transform identity and must therefore compare mismatched/stale until the
            // normal corpus or per-document reindex rebuilds it. Source document rows and extracted chunks stay intact.
            migrationBuilder.Sql(
                """
                UPDATE knowledge_documents
                SET vector_identity = 'legacy:unversioned',
                    vector_dim = 0;

                UPDATE knowledge_chunk_vectors
                SET vector_identity = 'legacy:unversioned';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunk_vectors_embedding_model_vector_identity_dim",
                table: "knowledge_chunk_vectors",
                columns: new[] { "embedding_model", "vector_identity", "dim" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_chunk_vectors_embedding_model_vector_identity_dim",
                table: "knowledge_chunk_vectors");

            migrationBuilder.DropColumn(
                name: "vector_dim",
                table: "knowledge_documents");

            migrationBuilder.DropColumn(
                name: "vector_identity",
                table: "knowledge_documents");

            migrationBuilder.DropColumn(
                name: "vector_identity",
                table: "knowledge_chunk_vectors");
        }
    }
}
