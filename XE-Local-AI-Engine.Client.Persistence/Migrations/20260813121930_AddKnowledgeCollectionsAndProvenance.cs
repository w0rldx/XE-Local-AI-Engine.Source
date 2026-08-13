using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddKnowledgeCollectionsAndProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_documents_content_hash",
                table: "knowledge_documents");

            migrationBuilder.AddColumn<string>(
                name: "chunker_version",
                table: "knowledge_documents",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "collection_id",
                table: "knowledge_documents",
                type: "TEXT",
                nullable: false,
                defaultValue: "DEFAULT");

            migrationBuilder.AddColumn<string>(
                name: "parser_version",
                table: "knowledge_documents",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "source_kind",
                table: "knowledge_documents",
                type: "TEXT",
                nullable: false,
                defaultValue: "upload");

            migrationBuilder.AddColumn<string>(
                name: "source_id",
                table: "knowledge_documents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_path",
                table: "knowledge_documents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "page_number",
                table: "knowledge_document_sections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                table: "knowledge_document_chunks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "content_kind",
                table: "knowledge_document_chunks",
                type: "TEXT",
                nullable: false,
                defaultValue: "text");

            migrationBuilder.AddColumn<string>(
                name: "embedding_input_hash",
                table: "knowledge_document_chunks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "end_offset",
                table: "knowledge_document_chunks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "knowledge_document_chunks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "page_number",
                table: "knowledge_document_chunks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_path",
                table: "knowledge_document_chunks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "start_offset",
                table: "knowledge_document_chunks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "symbol",
                table: "knowledge_document_chunks",
                type: "TEXT",
                nullable: true);

            // Expand the external-content FTS index from body-only retrieval to deterministic structure-aware fields.
            // Rebuilding from the content table preserves every existing row and keeps the stable rowid alignment.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS knowledge_document_chunks_au;
                DROP TRIGGER IF EXISTS knowledge_document_chunks_ad;
                DROP TRIGGER IF EXISTS knowledge_document_chunks_ai;
                DROP TABLE IF EXISTS chunk_fts;

                CREATE VIRTUAL TABLE chunk_fts USING fts5(
                    chunk_id UNINDEXED,
                    document_id UNINDEXED,
                    source_path,
                    heading_path,
                    symbol,
                    content,
                    content='knowledge_document_chunks',
                    content_rowid='rowid'
                );

                CREATE TRIGGER knowledge_document_chunks_ai AFTER INSERT ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(rowid, chunk_id, document_id, source_path, heading_path, symbol, content)
                    VALUES (new.rowid, new.chunk_id, new.document_id, new.source_path, new.heading_path, new.symbol, new.content);
                END;

                CREATE TRIGGER knowledge_document_chunks_ad AFTER DELETE ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(chunk_fts, rowid, chunk_id, document_id, source_path, heading_path, symbol, content)
                    VALUES ('delete', old.rowid, old.chunk_id, old.document_id, old.source_path, old.heading_path, old.symbol, old.content);
                END;

                CREATE TRIGGER knowledge_document_chunks_au AFTER UPDATE ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(chunk_fts, rowid, chunk_id, document_id, source_path, heading_path, symbol, content)
                    VALUES ('delete', old.rowid, old.chunk_id, old.document_id, old.source_path, old.heading_path, old.symbol, old.content);
                    INSERT INTO chunk_fts(rowid, chunk_id, document_id, source_path, heading_path, symbol, content)
                    VALUES (new.rowid, new.chunk_id, new.document_id, new.source_path, new.heading_path, new.symbol, new.content);
                END;

                INSERT INTO chunk_fts(chunk_fts) VALUES ('rebuild');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_documents_collection_id_content_hash",
                table: "knowledge_documents",
                columns: new[] { "collection_id", "content_hash" },
                unique: true,
                filter: "source_kind <> 'repository'");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_documents_collection_id_source_kind_source_id_source_path",
                table: "knowledge_documents",
                columns: new[] { "collection_id", "source_kind", "source_id", "source_path" },
                unique: true,
                filter: "source_kind = 'repository' AND source_id IS NOT NULL AND source_path IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_documents_collection_id_status",
                table: "knowledge_documents",
                columns: new[] { "collection_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_chunks_content_hash",
                table: "knowledge_document_chunks",
                column: "content_hash");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_chunks_embedding_input_hash",
                table: "knowledge_document_chunks",
                column: "embedding_input_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fail before any destructive schema change when the current collection-scoped identities cannot map back
            // to the old global content-hash uniqueness. Building a temporary unique index turns any duplicate hash
            // into a constraint failure; EF's migration transaction then leaves the upgraded schema/data untouched.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IX_knowledge_documents_down_hash_guard
                ON knowledge_documents(content_hash);
                DROP INDEX IX_knowledge_documents_down_hash_guard;
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS knowledge_document_chunks_au;
                DROP TRIGGER IF EXISTS knowledge_document_chunks_ad;
                DROP TRIGGER IF EXISTS knowledge_document_chunks_ai;
                DROP TABLE IF EXISTS chunk_fts;
                """);

            migrationBuilder.DropIndex(
                name: "IX_knowledge_documents_collection_id_content_hash",
                table: "knowledge_documents");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_documents_collection_id_status",
                table: "knowledge_documents");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_documents_collection_id_source_kind_source_id_source_path",
                table: "knowledge_documents");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_document_chunks_content_hash",
                table: "knowledge_document_chunks");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_document_chunks_embedding_input_hash",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "chunker_version",
                table: "knowledge_documents");

            migrationBuilder.DropColumn(
                name: "collection_id",
                table: "knowledge_documents");

            migrationBuilder.DropColumn(
                name: "parser_version",
                table: "knowledge_documents");

            migrationBuilder.DropColumn(
                name: "source_kind",
                table: "knowledge_documents");

            migrationBuilder.DropColumn(
                name: "source_id",
                table: "knowledge_documents");

            migrationBuilder.DropColumn(
                name: "source_path",
                table: "knowledge_documents");

            migrationBuilder.DropColumn(
                name: "page_number",
                table: "knowledge_document_sections");

            migrationBuilder.DropColumn(
                name: "content_hash",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "content_kind",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "embedding_input_hash",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "end_offset",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "language",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "page_number",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "source_path",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "start_offset",
                table: "knowledge_document_chunks");

            migrationBuilder.DropColumn(
                name: "symbol",
                table: "knowledge_document_chunks");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_documents_content_hash",
                table: "knowledge_documents",
                column: "content_hash",
                unique: true);

            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE chunk_fts USING fts5(
                    content,
                    chunk_id UNINDEXED,
                    document_id UNINDEXED,
                    content='knowledge_document_chunks',
                    content_rowid='rowid'
                );

                CREATE TRIGGER knowledge_document_chunks_ai AFTER INSERT ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(rowid, content, chunk_id, document_id)
                    VALUES (new.rowid, new.content, new.chunk_id, new.document_id);
                END;

                CREATE TRIGGER knowledge_document_chunks_ad AFTER DELETE ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(chunk_fts, rowid, content, chunk_id, document_id)
                    VALUES ('delete', old.rowid, old.content, old.chunk_id, old.document_id);
                END;

                CREATE TRIGGER knowledge_document_chunks_au AFTER UPDATE ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(chunk_fts, rowid, content, chunk_id, document_id)
                    VALUES ('delete', old.rowid, old.content, old.chunk_id, old.document_id);
                    INSERT INTO chunk_fts(rowid, content, chunk_id, document_id)
                    VALUES (new.rowid, new.content, new.chunk_id, new.document_id);
                END;

                INSERT INTO chunk_fts(chunk_fts) VALUES ('rebuild');
                """);
        }
    }
}
