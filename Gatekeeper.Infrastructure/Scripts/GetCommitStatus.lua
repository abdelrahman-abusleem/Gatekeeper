-- KEYS (all positions required; keys share the configured Redis Cluster slot)
-- KEYS[1] = Claim Attempt STRING: JSON for the claim AdmissionId.
-- KEYS[2] = Active Passes ZSET: user -> pass expiry (Unix milliseconds).
-- KEYS[3] = Active AdmissionId STRING: this user's current identity fence.
-- KEYS[4] = Pass Token STRING: this user's exact persisted JWT.
-- KEYS[5] = Processing ZSET: user -> lease expiry (Unix milliseconds).
-- KEYS[6] = Admission Records HASH: user -> reservation JSON.
--
-- ARGV (required unless marked optional or mode-specific)
-- ARGV[1] = UserId: nonempty string from the claim.
-- ARGV[2] = AdmissionId: canonical GUID D string from the same claim.
-- ARGV[3] = Signed JWT: exact prepared token string being reconciled.
-- ARGV[4] = Claim ID acceptance window: whole milliseconds, 1000..3600000.
-- ARGV[5] = Shared grace policy: whole milliseconds, 1000..300000 (validated; this read does not change TTL).
--
-- RETURNS
-- One {status}: COMMITTED, NOT_COMMITTED, UNKNOWN, MISMATCH, INCONSISTENT or EXPIRED.
-- Read-only reconciliation of the exact identity/token; released or recovered attempts are EXPIRED.
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
local window = tonumber(ARGV[4])
local grace = tonumber(ARGV[5])
if not window or window < 1000 or window > 3600000 or window % 1 ~= 0
    or not grace or grace < 1000 or grace > 300000 or grace % 1 ~= 0 then
    return redis.error_reply('Invalid attempt acceptance/grace settings')
end
local types = { 'string', 'zset', 'string', 'string', 'zset', 'hash' }
for i, expected in ipairs(types) do
    local actual = redis.call('TYPE', KEYS[i]).ok
    if actual ~= 'none' and actual ~= expected then return { 'INCONSISTENT' } end
end
local stored = redis.call('GET', KEYS[1])
if not stored then
    local time = redis.call('TIME')
    local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
    local lease = redis.call('ZSCORE', KEYS[5], ARGV[1])
    local raw = redis.call('HGET', KEYS[6], ARGV[1])
    local activeId = redis.call('GET', KEYS[3])
    local token = redis.call('GET', KEYS[4])
    local activeExpiry = redis.call('ZSCORE', KEYS[2], ARGV[1])
    if raw then
        local ok, record = pcall(cjson.decode, raw)
        if not ok or type(record) ~= 'table' or record.userId ~= ARGV[1]
            or not guidVersion(record.admissionId) or not lease
            or record.admissionId == ARGV[2] then return { 'INCONSISTENT' } end
    elseif lease then return { 'INCONSISTENT' } end
    if activeId == ARGV[2] then return { 'INCONSISTENT' } end
    if activeId or token then
        if not activeId or not guidVersion(activeId) or not token or not activeExpiry
            or tonumber(activeExpiry) <= now then return { 'INCONSISTENT' } end
    elseif activeExpiry and tonumber(activeExpiry) > now then return { 'INCONSISTENT' } end
    local version = guidVersion(ARGV[2])
    if version == 4 or (version == 7 and now >= uuidTime(ARGV[2]) + window) then return { 'EXPIRED' } end
    return { 'UNKNOWN' }
end
local ok, attempt = pcall(cjson.decode, stored)
if not ok or type(attempt) ~= 'table' then return { 'INCONSISTENT' } end
if attempt.userId ~= ARGV[1] or attempt.admissionId ~= ARGV[2] then
    return { 'MISMATCH' }
end
local time = redis.call('TIME')
local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
local activeId = redis.call('GET', KEYS[3])
local token = redis.call('GET', KEYS[4])
local activeExpiry = redis.call('ZSCORE', KEYS[2], ARGV[1])
local lease = redis.call('ZSCORE', KEYS[5], ARGV[1])
local record = redis.call('HGET', KEYS[6], ARGV[1])
if attempt.released ~= nil and type(attempt.released) ~= 'boolean' then return { 'INCONSISTENT' } end
if attempt.released == true then
    local expiry = tonumber(attempt.passExpiresAt)
    local score = tonumber(attempt.originalScore)
    local oldLease = tonumber(attempt.leaseExpiresAt)
    if attempt.status ~= 'COMMITTED' or not expiry or expiry <= 0 or expiry > 253402300799999 or expiry % 1 ~= 0
        or type(attempt.originalScore) ~= 'string' or not score or score < 1 or score > 9007199254740991 or score % 1 ~= 0
        or not oldLease or oldLease <= 0 or oldLease > 253402300799999 or oldLease % 1 ~= 0
        or activeId == ARGV[2] or token == ARGV[3] then return { 'INCONSISTENT' } end
    if record then
        local valid, data = pcall(cjson.decode, record)
        if not valid or type(data) ~= 'table' or data.userId ~= ARGV[1]
            or not guidVersion(data.admissionId) or data.admissionId == ARGV[2] then return { 'INCONSISTENT' } end
    elseif lease then return { 'INCONSISTENT' } end
    return { 'EXPIRED' }
end
if attempt.status == 'RECOVERED' then
    -- A resolved attempt can never be used to commit again.
    return { 'EXPIRED' }
end
if attempt.status ~= nil and attempt.status ~= 'COMMITTED' then return { 'INCONSISTENT' } end
if attempt.status ~= 'COMMITTED' then
    if activeId or token or activeExpiry then return { 'INCONSISTENT' } end
    local valid, data = pcall(cjson.decode, record or '')
    if not valid or type(data) ~= 'table' or not lease
        or data.userId ~= ARGV[1] or data.admissionId ~= ARGV[2]
        or data.originalScore ~= attempt.originalScore
        or tonumber(lease) ~= tonumber(attempt.leaseExpiresAt) then
        return { 'INCONSISTENT' }
    end
    if tonumber(lease) <= now then return { 'EXPIRED' } end
    return { 'NOT_COMMITTED' }
end
local expiry = tonumber(attempt.passExpiresAt)
if not expiry or expiry % 1 ~= 0 or expiry <= 0 then return { 'INCONSISTENT' } end
if activeId and activeId ~= ARGV[2] then return { 'MISMATCH' } end
if token and token ~= ARGV[3] then return { 'MISMATCH' } end
if activeExpiry and tonumber(activeExpiry) ~= expiry then return { 'MISMATCH' } end
if expiry <= now then return { 'EXPIRED' } end
if not activeId or not token or not activeExpiry or lease or record then
    return { 'INCONSISTENT' }
end
if redis.call('PEXPIRETIME', KEYS[3]) ~= expiry
    or redis.call('PEXPIRETIME', KEYS[4]) ~= expiry then
    return { 'INCONSISTENT' }
end
return { 'COMMITTED' }