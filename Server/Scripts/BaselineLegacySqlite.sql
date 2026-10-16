BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT '20261016133620_InitialSchema', '10.0.13'
WHERE EXISTS (SELECT 1 FROM "sqlite_master" WHERE "type" = 'table' AND "name" = 'AspNetUsers')
  AND NOT EXISTS (SELECT 1 FROM "__EFMigrationsHistory");

COMMIT;
