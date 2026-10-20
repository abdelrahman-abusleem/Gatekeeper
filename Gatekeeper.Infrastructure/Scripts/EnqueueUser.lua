-- KEYS (all positions required; keys share the configured Redis Cluster slot)
-- KEYS[1] = Waiting ZSET: user -> original FIFO sequence score.
-- KEYS[2] = Queue Sequence STRING: decimal integer counter incremented for new users only.
-- KEYS[3] = Processing ZSET: user -> lease expiry (Unix milliseconds).
-- KEYS[4] = Active Passes ZSET: user -> pass expiry (Unix milliseconds).
-- KEYS[5] = Admission Records HASH: user -> reservation JSON.
--
-- ARGV (required unless marked optional or mode-specific)
-- ARGV[1] = UserId: required nonempty string.
-- ARGV[2] = Waiting limit: optional integer 0..2147483647, defaults to 0 (unlimited); C# always supplies it.
--
-- RETURNS
-- {'QUEUED', rankText}: zero-based decimal rank for a new or already-Waiting user.
-- {status}: PROCESSING, ACTIVE, RECORD_CONFLICT or QUEUE_FULL.
-- Invalid inputs/key types or an exhausted/invalid sequence raise Redis errors.
--
local userId = ARGV[1]
local limit = tonumber(ARGV[2] or '0')
if not limit or limit < 0 or limit > 2147483647 or limit % 1 ~= 0 then
    return redis.error_reply('Invalid Waiting limit')
end

local types = { 'zset', 'string', 'zset', 'zset', 'hash' }
for i, expected in ipairs(types) do
    local actual = redis.call('TYPE', KEYS[i]).ok
    if actual ~= 'none' and actual ~= expected then
        return redis.error_reply('Inconsistent enqueue key type')
    end
end

if not userId or userId == '' then
    return redis.error_reply('Invalid UserId')
end


if redis.call('ZSCORE', KEYS[3], userId) then
    return { 'PROCESSING' }
end


if redis.call('HEXISTS', KEYS[5], userId) == 1 then
    return { 'RECORD_CONFLICT' }
end

local time = redis.call('TIME')

local nowMs =
    tonumber(time[1]) * 1000
    + math.floor(tonumber(time[2]) / 1000)

local activeExpiresAt =
    redis.call('ZSCORE', KEYS[4], userId)

if activeExpiresAt
    and tonumber(activeExpiresAt) > nowMs then
    return { 'ACTIVE' }
end


local existingScore =
    redis.call('ZSCORE', KEYS[1], userId)

if existingScore then
    local rank = redis.call(
        'ZRANK',
        KEYS[1],
        userId
    )

    return { 'QUEUED', tostring(rank) }
end

-- Existing accepted users above retain their semantics. Recovery does not use this new-entry gate.
if limit > 0 and redis.call('ZCARD', KEYS[1]) >= limit then return { 'QUEUE_FULL' } end

-- Doubles represent integers exactly only through 2^53. Stop before ambiguity.
local current = redis.call('GET', KEYS[2])
if current and (not string.match(current, '^%d+$') or not tonumber(current)
    or tonumber(current) > 9007199254740990) then
    return redis.error_reply('Waiting sequence exhausted or invalid; operator intervention required')
end
redis.call('INCR', KEYS[2])
-- Keep the exact decimal text; Lua tostring can round large integer scores.
local newScore = redis.call('GET', KEYS[2])

redis.call(
    'ZADD',
    KEYS[1],
    'NX',
    newScore,
    userId
)

local rank = redis.call(
    'ZRANK',
    KEYS[1],
    userId
)

return { 'QUEUED', tostring(rank) }