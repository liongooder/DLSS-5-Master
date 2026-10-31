-- 0.9.8: the PC a report ran on besides its graphics card, so every report on a game's page shows it.
-- Reports sent by older versions keep '' (memory 0).
-- Apply before deploying the 0.9.8 worker:
--   npx wrangler d1 execute dlss5master-community --remote --file schema-v4.sql
ALTER TABLE reports ADD COLUMN driver TEXT NOT NULL DEFAULT '';   -- graphics driver: "581.57" (NVIDIA's numbering) or Windows' "32.0.101.6979"
ALTER TABLE reports ADD COLUMN cpu TEXT NOT NULL DEFAULT '';      -- "AMD Ryzen 7 7800X3D"
ALTER TABLE reports ADD COLUMN ram_gb INTEGER NOT NULL DEFAULT 0; -- installed memory
ALTER TABLE reports ADD COLUMN os TEXT NOT NULL DEFAULT '';       -- "Windows 11 24H2 (26100)"
