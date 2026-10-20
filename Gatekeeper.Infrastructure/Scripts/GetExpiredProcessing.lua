-- KEYS (all positions required; keys share the configured Redis Cluster slot)
-- KEYS[1] = Processing ZSET: user -> lease expiry (Unix milliseconds).
--
-- ARGV (required unless marked optional or mode-specific)
-- ARGV[1] = Page size: required positive integer maximum number of expired users to select.
-- ARGV[2] = Next rank offset: required nonnegative integer cursor retained per C# store (initially 0).
--
-- RETURNS
-- {nextRankOffset, {userId, ...}}: integer cursor and array of expired users; {0, {}} if none.
-- Selects scores <= Redis TIME in milliseconds; selection is read-only and is not Recovery.
--
local time = redis.call('TIME')
local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
local count = redis.call('ZCOUNT', KEYS[1], '-inf', now)
if count == 0 then return { 0, {} } end
local first = tonumber(ARGV[2]) % count
local last = math.min(first + tonumber(ARGV[1]) - 1, count - 1)
-- Expired leases form a rank prefix. Rank access avoids an O(offset) score scan,
-- handles equal lease scores, and moves past retained corrupt reservations.
local users = redis.call('ZRANGE', KEYS[1], first, last)
return { (last + 1) % count, users }