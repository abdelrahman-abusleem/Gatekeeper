-- CONTRACT: KEYS (all positions required; keys share the configured Redis Cluster slot)
--   KEYS[1] = Waiting ZSET: user -> original FIFO sequence score.
--   KEYS[2] = Processing ZSET: user -> lease expiry (Unix milliseconds).
--   KEYS[3] = Admission Records HASH: user -> reservation JSON.
--   KEYS[4] = Active Passes ZSET: user -> pass expiry (Unix milliseconds).
--   KEYS[5] = Claim Attempt STRING: JSON for the requested AdmissionId.
--
-- ARGV (required unless marked optional or mode-specific)
-- ARGV[1] = AdmissionId: canonical lowercase GUID D string; v7 for new attempts, recorded v4 retries supported.
-- ARGV[2] = MaxActivePasses: positive integer shared Active + Processing capacity.
-- ARGV[3] = Processing lease: whole milliseconds, 1..3600000.
-- ARGV[4] = Claim ID acceptance window: whole milliseconds, 1000..3600000.
-- ARGV[5] = Shared future-skew/retention grace: whole milliseconds, 1000..300000.
--
-- RETURNS
-- {'CLAIMED', user, AdmissionId, OriginalScore, leaseExpiryUnixMs, redisNowUnixMs};
-- User and AdmissionId are strings; OriginalScore and both timestamps are decimal strings.
-- Otherwise {status}: CAPACITY_FULL, QUEUE_EMPTY, CLAIM_EXPIRED, ATTEMPT_RESOLVED,
-- ID_EXPIRED, ID_TOO_FUTURE, ID_UNSUPPORTED or ID_INVALID. Invalid inputs/state may raise Redis errors.
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



-- KEYS and ARGV processing below follows the positional contract documented above.
local admissionId = ARGV[1]
local maxCapacity = tonumber(ARGV[2])
local leaseMs = tonumber(ARGV[3])
local window = tonumber(ARGV[4])
local grace = tonumber(ARGV[5])
if not window or window < 1000 or window > 3600000 or window % 1 ~= 0
    or not grace or grace < 1000 or grace > 300000 or grace % 1 ~= 0 then
    return redis.error_reply('Invalid attempt acceptance/grace settings')
end

local types = { 'zset', 'zset', 'hash', 'zset', 'string' }
for i, expected in ipairs(types) do
    local actual = redis.call('TYPE', KEYS[i]).ok
    if actual ~= 'none' and actual ~= expected then
        return redis.error_reply('Inconsistent claim key type')
    end
end

if not admissionId or admissionId == '' then
    return redis.error_reply('Invalid AdmissionId')
end

if not maxCapacity
    or maxCapacity < 1
    or maxCapacity % 1 ~= 0 then
    return redis.error_reply('Invalid MaxActivePasses')
end

if not leaseMs
    or leaseMs < 1
    or leaseMs > 3600000
    or leaseMs % 1 ~= 0 then
    return redis.error_reply('Invalid processing lease')
end

local time = redis.call('TIME')

local nowMs =
    tonumber(time[1]) * 1000
    + math.floor(tonumber(time[2]) / 1000)


local existingAttempt =
    redis.call('GET', KEYS[5])

if existingAttempt then
    local ok, attempt = pcall(cjson.decode, existingAttempt)
    if not ok or type(attempt) ~= 'table' then
        return redis.error_reply('Malformed claim attempt')
    end

    if attempt.admissionId ~= admissionId then
        return redis.error_reply(
            'Claim attempt identity conflict'
        )
    end

    if attempt.status == 'QUEUE_EMPTY' or attempt.status == 'CAPACITY_FULL' then
        return { attempt.status }
    end
    if type(attempt.userId) ~= 'string' or attempt.userId == '' then
        return redis.error_reply('Malformed claim user')
    end

    if attempt.status == 'COMMITTED' or attempt.status == 'RECOVERED' then
        return { 'ATTEMPT_RESOLVED' }
    end
    if attempt.status ~= nil then return redis.error_reply('Invalid attempt status') end

    local userId = attempt.userId

    local processingScore =
        redis.call('ZSCORE', KEYS[2], userId)

    local storedRecord =
        redis.call('HGET', KEYS[3], userId)


    if not processingScore or not storedRecord then
        return { 'ATTEMPT_RESOLVED' }
    end

    local valid, record = pcall(cjson.decode, storedRecord)
    if not valid or type(record) ~= 'table' then
        return redis.error_reply('Malformed admission record')
    end

    if record.admissionId ~= admissionId then
        return { 'ATTEMPT_RESOLVED' }
    end

    if record.userId ~= userId or record.originalScore ~= attempt.originalScore
        or type(attempt.originalScore) ~= 'string'
        or not tonumber(attempt.originalScore)
        or tonumber(attempt.originalScore) < 1
        or tonumber(attempt.originalScore) > 9007199254740991
        or tonumber(attempt.originalScore) % 1 ~= 0 then
        return redis.error_reply('Inconsistent admission record')
    end


    if tonumber(processingScore)
        ~= tonumber(attempt.leaseExpiresAt) then
        return { 'ATTEMPT_RESOLVED' }
    end

    if tonumber(processingScore) <= nowMs then
        return { 'CLAIM_EXPIRED' }
    end


    return {
        'CLAIMED',
        userId,
        admissionId,
        attempt.originalScore,
        tostring(attempt.leaseExpiresAt),
        tostring(nowMs)
    }
end


-- Recorded attempts above remain reconcilable regardless of age/version.
local version = guidVersion(admissionId)
if not version then return { 'ID_INVALID' } end
if version ~= 7 then return { 'ID_UNSUPPORTED' } end
local created = uuidTime(admissionId)
if nowMs >= created + window then return { 'ID_EXPIRED' } end
if created > nowMs + grace then return { 'ID_TOO_FUTURE' } end

redis.call(
    'ZREMRANGEBYSCORE',
    KEYS[4],
    '-inf',
    nowMs
)


local activeCount =
    redis.call('ZCARD', KEYS[4])

local processingCount =
    redis.call('ZCARD', KEYS[2])

if activeCount + processingCount >= maxCapacity then
    local deadline = created + window + grace
    redis.call('SET', KEYS[5], cjson.encode({ admissionId = admissionId, status = 'CAPACITY_FULL' }), 'PXAT', deadline)
    return { 'CAPACITY_FULL' }
end

local nextUser = redis.call(
    'ZRANGE',
    KEYS[1],
    0,
    0,
    'WITHSCORES'
)

if #nextUser == 0 then
    local deadline = created + window + grace
    redis.call('SET', KEYS[5], cjson.encode({ admissionId = admissionId, status = 'QUEUE_EMPTY' }), 'PXAT', deadline)
    return { 'QUEUE_EMPTY' }
end

local userId = nextUser[1]
local originalScore = nextUser[2]

local score = tonumber(originalScore)
if not score or score < 1 or score > 9007199254740991 or score % 1 ~= 0 then
    return redis.error_reply('Invalid waiting sequence score')
end

if redis.call('ZSCORE', KEYS[2], userId)
    or redis.call('ZSCORE', KEYS[4], userId)
    or redis.call('HEXISTS', KEYS[3], userId) == 1 then

    return redis.error_reply(
        'User already has an admission state'
    )
end

local leaseExpiresAt = nowMs + leaseMs

local admissionRecord = cjson.encode({
    userId = userId,
    admissionId = admissionId,
    originalScore = originalScore
})

local claimAttempt = cjson.encode({
    userId = userId,
    admissionId = admissionId,
    originalScore = originalScore,
    leaseExpiresAt = leaseExpiresAt
})
redis.call('ZREM', KEYS[1], userId)

redis.call(
    'ZADD',
    KEYS[2],
    leaseExpiresAt,
    userId
)

redis.call(
    'HSET',
    KEYS[3],
    userId,
    admissionRecord
)

redis.call(
    'SET',
    KEYS[5],
    claimAttempt
)

return {
    'CLAIMED',
    userId,
    admissionId,
    originalScore,
    tostring(leaseExpiresAt),
    tostring(nowMs)
}