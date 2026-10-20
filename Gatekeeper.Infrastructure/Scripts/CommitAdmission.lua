-- KEYS (all positions required; keys share the configured Redis Cluster slot)
-- KEYS[1] = Processing ZSET: user -> lease expiry (Unix milliseconds).
-- KEYS[2] = Admission Records HASH: user -> reservation JSON.
-- KEYS[3] = Active Passes ZSET: user -> pass expiry (Unix milliseconds).
-- KEYS[4] = Pass Token STRING: this user's exact persisted JWT.
-- KEYS[5] = Claim Attempt STRING: JSON for the claim AdmissionId.
-- KEYS[6] = Active AdmissionId STRING: this user's current identity fence.
--
-- ARGV (required unless marked optional or mode-specific)
-- ARGV[1] = UserId: nonempty string from the claim.
-- ARGV[2] = AdmissionId: canonical GUID D string from the same claim.
-- ARGV[3] = Signed JWT: nonempty exact token string prepared for this claim.
-- ARGV[4] = Pass expiry: positive integer UTC Unix milliseconds.
-- ARGV[5] = OriginalScore: exact positive integer FIFO sequence from the claim.
-- ARGV[6] = Observed lease expiry: integer UTC Unix milliseconds from the claim.
-- ARGV[7] = Claim ID acceptance window: whole milliseconds, 1000..3600000.
-- ARGV[8] = Shared retention grace: whole milliseconds, 1000..300000.
--
-- RETURNS
-- {'COMMITTED'} or one rejection {status}: INCONSISTENT, NOT_PROCESSING, LEASE_EXPIRED,
-- RECORD_MISSING, ADMISSION_MISMATCH, ATTEMPT_MISSING, ATTEMPT_MISMATCH, LEASE_MISMATCH,
-- ORIGINAL_SCORE_MISMATCH, INVALID_PASS_EXPIRATION, ALREADY_ACTIVE, ACTIVE_ID_EXISTS or
-- TOKEN_EXISTS. Invalid input/policy may raise Redis errors; rejection is not reconciliation.
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


local userId = ARGV[1]
local admissionId = ARGV[2]
local token = ARGV[3]
local expiresAtMs = tonumber(ARGV[4])
local window = tonumber(ARGV[7])
local grace = tonumber(ARGV[8])
if not window or window < 1000 or window > 3600000 or window % 1 ~= 0
    or not grace or grace < 1000 or grace > 300000 or grace % 1 ~= 0 then
    return redis.error_reply('Invalid attempt acceptance/grace settings')
end

-- Check all types before writing; Redis Lua does not roll back runtime errors.
local types = { 'zset', 'hash', 'zset', 'string', 'string', 'string' }
for i, expected in ipairs(types) do
    local actual = redis.call('TYPE', KEYS[i]).ok
    if actual ~= 'none' and actual ~= expected then return { 'INCONSISTENT' } end
end

if not userId or userId == '' then
    return redis.error_reply('Invalid UserId')
end

if not admissionId or admissionId == '' then
    return redis.error_reply('Invalid AdmissionId')
end

if not token or token == '' then
    return redis.error_reply('Invalid token')
end

if not expiresAtMs
    or expiresAtMs <= 0
    or expiresAtMs > 9007199254740991 - grace
    or expiresAtMs % 1 ~= 0 then
    return redis.error_reply('Invalid pass expiration')
end

local time = redis.call('TIME')

local nowMs =
    tonumber(time[1]) * 1000
    + math.floor(tonumber(time[2]) / 1000)

local leaseExpiresAt =
    redis.call('ZSCORE', KEYS[1], userId)

if not leaseExpiresAt then
    return { 'NOT_PROCESSING' }
end

if tonumber(leaseExpiresAt) <= nowMs then
    return { 'LEASE_EXPIRED' }
end

local storedRecord =
    redis.call('HGET', KEYS[2], userId)

if not storedRecord then
    return { 'RECORD_MISSING' }
end

local recordOk, record = pcall(cjson.decode, storedRecord)
if not recordOk or type(record) ~= 'table' or record.userId ~= userId then
    return { 'INCONSISTENT' }
end

if record.admissionId ~= admissionId then
    return { 'ADMISSION_MISMATCH' }
end

local storedAttempt =
    redis.call('GET', KEYS[5])

if not storedAttempt then
    return { 'ATTEMPT_MISSING' }
end

local attemptOk, attempt = pcall(cjson.decode, storedAttempt)
if not attemptOk or type(attempt) ~= 'table' or attempt.status ~= nil then
    return { 'INCONSISTENT' }
end

if attempt.userId ~= userId
    or attempt.admissionId ~= admissionId then

    return { 'ATTEMPT_MISMATCH' }
end

if tonumber(attempt.leaseExpiresAt)
    ~= tonumber(leaseExpiresAt) then

    return { 'LEASE_MISMATCH' }
end

if tonumber(ARGV[6]) ~= tonumber(leaseExpiresAt) then return { 'LEASE_MISMATCH' } end

local originalScore = tonumber(record.originalScore)
if type(record.originalScore) ~= 'string' or not originalScore
    or originalScore < 1 or originalScore > 9007199254740991 or originalScore % 1 ~= 0
    or attempt.originalScore ~= record.originalScore then

    return { 'ORIGINAL_SCORE_MISMATCH' }
end

if tonumber(ARGV[5]) ~= originalScore then return { 'ORIGINAL_SCORE_MISMATCH' } end

if expiresAtMs <= nowMs then
    return { 'INVALID_PASS_EXPIRATION' }
end

if redis.call('ZSCORE', KEYS[3], userId) then
    return { 'ALREADY_ACTIVE' }
end

if redis.call('EXISTS', KEYS[6]) == 1 then
    return { 'ACTIVE_ID_EXISTS' }
end

if redis.call('EXISTS', KEYS[4]) == 1 then return { 'TOKEN_EXISTS' } end
attempt.status = 'COMMITTED'
attempt.passExpiresAt = expiresAtMs
local committedAttempt = cjson.encode(attempt)
local version = guidVersion(admissionId)
if version ~= 4 and version ~= 7 then return { 'INCONSISTENT' } end
local cutoff = version == 7 and (uuidTime(admissionId) + window) or nowMs
local retentionDeadline = math.max(expiresAtMs, cutoff) + grace

redis.call(
    'SET',
    KEYS[4],
    token,
    'PXAT',
    expiresAtMs
)

redis.call(
    'SET',
    KEYS[6],
    admissionId,
    'PXAT',
    expiresAtMs
)

redis.call(
    'ZADD',
    KEYS[3],
    expiresAtMs,
    userId
)

redis.call(
    'ZREM',
    KEYS[1],
    userId
)

redis.call(
    'HDEL',
    KEYS[2],
    userId
)

redis.call(
    'SET',
    KEYS[5],
    committedAttempt,
    'PXAT',
    retentionDeadline
)

return { 'COMMITTED' }