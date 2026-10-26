-- 0.8.0: the reported game's rendering API (dx12, dx11, vulkan…), for the Community → Games filter.
-- Reports sent by older versions keep ''.
ALTER TABLE reports ADD COLUMN api TEXT NOT NULL DEFAULT '';
