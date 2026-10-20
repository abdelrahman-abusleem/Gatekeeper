-- KEYS (all positions required; keys share the configured Redis Cluster slot)
-- KEYS[1] = Waiting ZSET: user -> original FIFO sequence score restored by Recovery.
-- KEYS[2] = Processing ZSET: user -> lease expiry (Unix milliseconds).
-- KEYS[3] = Admission Records HASH: user -> reservation JSON.
-- KEYS[4] = Active Passes ZSET: user -> pass expiry (Unix milliseconds).
-- KEYS[5] = Claim Attempt STRING: JSON for the claim AdmissionId.
-- KEYS[6] = Active AdmissionId STRING: this user's current identity fence.
-- KEYS[7] = Pass Token STRING: this user's persisted JWT; must not conflict with Recovery.
--
-- ARGV (required unless marked optional or mode-specific)
-- ARGV[1] = UserId: nonempty string from the claim.
-- ARGV[2] = AdmissionId: canonical GUID D string from the same claim.
-- ARGV[3] = OriginalScore: exact positive integer FIFO sequence from the claim.
-- ARGV[4] = Observed lease expiry: integer UTC Unix milliseconds from the claim.
-- ARGV[5] = Claim ID acceptance window: whole milliseconds, 1000..3600000.
-- ARGV[6] = Shared retention grace: whole milliseconds, 1000..300000.
--
-- RETURNS
-- One {status}: RECOVERED, NOT_EXPIRED, ALREADY_RESOLVED or INCONSISTENT.
-- Only a matching expired reservation is restored; conflicting state is not repaired speculatively.
-- Invalid acceptance/grace policy raises a Redis error.
--
-- Canonical Guid D text, not the mixed-endian in-memory .NET byte layout.
local function guidVersion(id)
    if type(id) ~= 'string' or #id ~= 36 or id ~= string.lower(id)
        or string.sub(id, 9, 9) ~= '-' or string.sub(id, 14, 14) ~= '-'
        or string.sub(id, 19, 19) ~= '-' or string.sub(id, 24, 24) ~= '-' then return nil end
    local hex = string.gsub(id, '-', '')
    if #hex ~= 32 or not string.match(hex, '^%x+$')
        or not string.find('89ab', string.sub(id, 20, 20), 1, true) then return nil end
    return tonumber(string.sub(id, 15, 15), 16)
end
local function uuidTime(id)
    return tonumber(string.sub(id, 1, 8) .. string.sub(id, 10, 13), 16)
end
local window = tonumber(ARGV[5])
local grace = tonumber(ARGV[6])
if not window or window < 1000 or window > 3600000 or window % 1 ~= 0
    or not grace or grace < 1000 or grace > 300000 or grace % 1 ~= 0 then
    return redis.error_reply('Invalid attempt acceptance/grace settings')
end
local types = { 'zset', 'zset', 'hash', 'zset', 'string', 'string', 'string' }
for i, expected in ipairs(types) do
    local actual = redis.call('TYPE', KEYS[i]).ok
    if actual ~= 'none' and actual ~= expected then return { 'INCONSISTENT' } end
end
local userId, admissionId = ARGV[1], ARGV[2]
local lease = redis.call('ZSCORE', KEYS[2], userId)
local raw = redis.call('HGET', KEYS[3], userId)
local stored = redis.call('GET', KEYS[5])
local ok, attempt = pcall(cjson.decode, stored or '')
if not ok or type(attempt) ~= 'table' then return { 'INCONSISTENT' } end
if attempt.userId ~= userId or attempt.admissionId ~= admissionId then return { 'INCONSISTENT' } end
if attempt.status == 'RECOVERED' or attempt.status == 'COMMITTED' then return { 'ALREADY_RESOLVED' } end
if attempt.status ~= nil then return { 'INCONSISTENT' } end
if not lease or not raw then return { 'INCONSISTENT' } end
local valid, record = pcall(cjson.decode, raw)
if not valid or type(record) ~= 'table' then return { 'INCONSISTENT' } end
if record.admissionId ~= admissionId then return { 'ALREADY_RESOLVED' } end
local score = tonumber(record.originalScore)
if record.userId ~= userId or type(record.originalScore) ~= 'string'
    or not score or score < 1 or score > 9007199254740991 or score % 1 ~= 0
    or record.originalScore ~= attempt.originalScore
    or score ~= tonumber(ARGV[3])
    or tonumber(lease) ~= tonumber(attempt.leaseExpiresAt)
    or tonumber(lease) ~= tonumber(ARGV[4]) then return { 'INCONSISTENT' } end
local time = redis.call('TIME')
local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
if tonumber(lease) > now then return { 'NOT_EXPIRED' } end
-- Never guess whether a conflicting Active state or duplicate Waiting is safe to erase.
if redis.call('ZSCORE', KEYS[4], userId)
    or redis.call('EXISTS', KEYS[6]) == 1 or redis.call('EXISTS', KEYS[7]) == 1
    or redis.call('ZSCORE', KEYS[1], userId) then return { 'INCONSISTENT' } end
attempt.status = 'RECOVERED'
local resolved = cjson.encode(attempt)
local version = guidVersion(admissionId)
if version ~= 4 and version ~= 7 then return { 'INCONSISTENT' } end
local cutoff = version == 7 and (uuidTime(admissionId) + window) or now
local deadline = math.max(now, cutoff) + grace
-- Validate and encode before the first write: Lua errors do not roll back writes.
redis.call('ZADD', KEYS[1], record.originalScore, userId)
redis.call('ZREM', KEYS[2], userId)
redis.call('HDEL', KEYS[3], userId)
redis.call('SET', KEYS[5], resolved, 'PXAT', deadline)
return { 'RECOVERED' }