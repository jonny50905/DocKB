CREATE TABLE files (
    id              BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    relative_path   VARCHAR(1024) NOT NULL,
    absolute_path   VARCHAR(1024) NOT NULL,
    file_name       VARCHAR(512)  NOT NULL,
    file_type       ENUM('word','excel') NOT NULL,
    size_bytes      BIGINT       NOT NULL,
    mtime_utc       DATETIME(6)  NOT NULL,
    sha256          CHAR(64)     NOT NULL,
    status          ENUM('active','deleted','failed') NOT NULL DEFAULT 'active',
    last_error      TEXT         NULL,
    markdown_full   LONGTEXT     NULL,
    ingested_at_utc DATETIME(6)  NOT NULL,
    created_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_files_relpath (relative_path),
    KEY ix_files_sha256 (sha256),
    KEY ix_files_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE chunks (
    id             BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    file_id        BIGINT       NOT NULL,
    ordinal        INT          NOT NULL,
    chunk_type     ENUM('word_section','excel_sheet_summary','excel_rows') NOT NULL,
    title_path     VARCHAR(1024) NULL,
    locator_json   JSON         NULL,
    content_md     MEDIUMTEXT   NOT NULL,
    char_len       INT          NOT NULL,
    es_indexed_at  DATETIME(6)  NULL,
    created_at     DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY uq_chunks_file_ord (file_id, ordinal),
    KEY ix_chunks_file (file_id),
    CONSTRAINT fk_chunks_file FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE ingestion_runs (
    id              BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    started_at_utc  DATETIME(6)  NOT NULL,
    finished_at_utc DATETIME(6)  NULL,
    scanned_count   INT          NOT NULL DEFAULT 0,
    new_count       INT          NOT NULL DEFAULT 0,
    updated_count   INT          NOT NULL DEFAULT 0,
    deleted_count   INT          NOT NULL DEFAULT 0,
    failed_count    INT          NOT NULL DEFAULT 0,
    notes           TEXT         NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
