using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XE_Local_AI_Engine.Client.Persistence.Migrations.NodeChatDb
{
    /// <inheritdoc />
    public partial class AddKnowledgeBaseTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_documents",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    original_file_name = table.Column<byte[]>(type: "BLOB", nullable: false),
                    mime_type = table.Column<string>(type: "TEXT", nullable: false),
                    extension = table.Column<string>(type: "TEXT", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    content_hash = table.Column<string>(type: "TEXT", nullable: false),
                    storage_path = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    failure_reason = table.Column<string>(type: "TEXT", nullable: true),
                    chunk_count = table.Column<int>(type: "INTEGER", nullable: false),
                    embedding_model = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_documents", x => x.document_id);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_document_sections",
                columns: table => new
                {
                    section_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    heading = table.Column<string>(type: "TEXT", nullable: true),
                    level = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_document_sections", x => x.section_id);
                    table.ForeignKey(
                        name: "FK_knowledge_document_sections_knowledge_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "knowledge_documents",
                        principalColumn: "document_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_document_chunks",
                columns: table => new
                {
                    rowid = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    chunk_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    section_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    chunk_index = table.Column<int>(type: "INTEGER", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    token_count = table.Column<int>(type: "INTEGER", nullable: false),
                    heading_path = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_document_chunks", x => x.rowid);
                    table.UniqueConstraint("AK_knowledge_document_chunks_chunk_id", x => x.chunk_id);
                    table.ForeignKey(
                        name: "FK_knowledge_document_chunks_knowledge_document_sections_section_id",
                        column: x => x.section_id,
                        principalTable: "knowledge_document_sections",
                        principalColumn: "section_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_knowledge_document_chunks_knowledge_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "knowledge_documents",
                        principalColumn: "document_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_chunk_vectors",
                columns: table => new
                {
                    chunk_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    dim = table.Column<int>(type: "INTEGER", nullable: false),
                    embedding = table.Column<byte[]>(type: "BLOB", nullable: false),
                    embedding_model = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_chunk_vectors", x => x.chunk_id);
                    table.ForeignKey(
                        name: "FK_knowledge_chunk_vectors_knowledge_document_chunks_chunk_id",
                        column: x => x.chunk_id,
                        principalTable: "knowledge_document_chunks",
                        principalColumn: "chunk_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_knowledge_chunk_vectors_knowledge_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "knowledge_documents",
                        principalColumn: "document_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunk_vectors_document_id",
                table: "knowledge_chunk_vectors",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunk_vectors_embedding_model",
                table: "knowledge_chunk_vectors",
                column: "embedding_model");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_chunks_document_id_chunk_index",
                table: "knowledge_document_chunks",
                columns: new[] { "document_id", "chunk_index" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_chunks_section_id",
                table: "knowledge_document_chunks",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_sections_document_id_ordinal",
                table: "knowledge_document_sections",
                columns: new[] { "document_id", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_documents_content_hash",
                table: "knowledge_documents",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_documents_status",
                table: "knowledge_documents",
                column: "status");

            // M5 (hand-added — see plan section 6): FTS5 external-content index over the plaintext chunk table plus the
            // three sync triggers. External content keeps only the inverted index here; the chunk text stays canonical in
            // knowledge_document_chunks, aligned by its integer rowid alias (content_rowid). That rowid is stable across a
            // database vacuum; if node-chat.db is ever vacuumed, rebuild the index with the fts5 'rebuild' command.
            migrationBuilder.Sql(@"
                CREATE VIRTUAL TABLE chunk_fts USING fts5(
                    content,
                    chunk_id UNINDEXED,
                    document_id UNINDEXED,
                    content='knowledge_document_chunks',
                    content_rowid='rowid'
                );");

            migrationBuilder.Sql(@"
                CREATE TRIGGER knowledge_document_chunks_ai AFTER INSERT ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(rowid, content, chunk_id, document_id)
                    VALUES (new.rowid, new.content, new.chunk_id, new.document_id);
                END;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER knowledge_document_chunks_ad AFTER DELETE ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(chunk_fts, rowid, content, chunk_id, document_id)
                    VALUES ('delete', old.rowid, old.content, old.chunk_id, old.document_id);
                END;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER knowledge_document_chunks_au AFTER UPDATE ON knowledge_document_chunks BEGIN
                    INSERT INTO chunk_fts(chunk_fts, rowid, content, chunk_id, document_id)
                    VALUES ('delete', old.rowid, old.content, old.chunk_id, old.document_id);
                    INSERT INTO chunk_fts(rowid, content, chunk_id, document_id)
                    VALUES (new.rowid, new.content, new.chunk_id, new.document_id);
                END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the FTS triggers + external-content index first (they reference knowledge_document_chunks).
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS knowledge_document_chunks_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS knowledge_document_chunks_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS knowledge_document_chunks_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS chunk_fts;");

            migrationBuilder.DropTable(
                name: "knowledge_chunk_vectors");

            migrationBuilder.DropTable(
                name: "knowledge_document_chunks");

            migrationBuilder.DropTable(
                name: "knowledge_document_sections");

            migrationBuilder.DropTable(
                name: "knowledge_documents");
        }
    }
}
