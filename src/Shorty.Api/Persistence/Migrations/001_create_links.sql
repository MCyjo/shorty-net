-- One row, one counter per kind of id. Instances reserve whole blocks with a single UPDATE (see PostgresIdBlockStore).
CREATE TABLE id_counter (
    name    text   PRIMARY KEY,
    next_id bigint NOT NULL
);

INSERT INTO id_counter (name, next_id) VALUES ('links', 0);

CREATE TABLE links (
    id         bigint      PRIMARY KEY,
    code       text        NOT NULL,
    url        text        NOT NULL,
    is_custom  boolean     NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NULL
);

-- The redirect path reads by code, and this index is also what makes codes unique: the database, not the
-- application, is the final judge when two writers race for the same code or alias.
CREATE UNIQUE INDEX ix_links_code ON links (code);
