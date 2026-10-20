-- KEYS (all positions required; keys share the configured Redis Cluster slot)
-- KEYS[1] = Waiting ZSET: user -> FIFO sequence score (must not conflict with Active).
-- KEYS[2] = Processing ZSET: user -> lease expiry (must not conflict with Active).
-- KEYS[3] = Admission Records HASH: user -> reservation JSON (must not conflict with Active).
-- KEYS[4] = Active Passes ZSET: user -> pass expiry (Unix milliseconds).
-- KEYS[5] = Pass Token STRING: this user's persisted JWT to delete on successful release.
-- KEYS[6] = Active AdmissionId STRING: this user's current identity fence.
-- KEYS[7] = Claim Attempt STRING: JSON for the expected AdmissionId, retaining COMMITTED with released=true.
--
-- ARGV (required unless marked optional or mode-specific)
-- ARGV[1] = UserId: required host-authorized string.
-- ARGV[2] = Expected AdmissionId: required canonical GUID D string; stale identities cannot release a replacement.
--
-- RETURNS
-- {'RELEASED'}, {'NOT_CURRENT'} or {'INCONSISTENT'}.
-- Release deletes only the matching current Active state; KEEPTTL preserves the attempt deadline.
--
local types = { 'zset', 'zset', 'hash', 'zset', 'string', 'string', 'string' }
for i, expected in ipairs(types) do
    local actual = redis.call('TYPE', KEYS[i]).ok
    if actual ~= 'none' and actual ~= expected then return { 'INCONSISTENT' } end
end
local user = ARGV[1]
local id = ARGV[2]
local time = redis.call('TIME')
local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
local identity = redis.call('GET', KEYS[6])
-- Never delete a later admission or confuse a Processing reservation with Active.
if identity and identity ~= id then return { 'NOT_CURRENT' } end
local expiry = tonumber(redis.call('ZSCORE', KEYS[4], user))
local token = redis.call('GET', KEYS[5])
if not identity and not token and (not expiry or expiry <= now) then return { 'NOT_CURRENT' } end
if #id ~= 36 or not string.find('47', string.sub(id, 15, 15), 1, true)
    or not string.find('89ab', string.sub(id, 20, 20), 1, true) then return { 'INCONSISTENT' } end
local function integer(value, maximum)
    local n = tonumber(value)
    return n and n > 0 and n <= maximum and n % 1 == 0
end
if not identity or not token or token == '' or not integer(expiry, 253402300799999) or expiry <= now
    or redis.call('PEXPIRETIME', KEYS[5]) ~= expiry or redis.call('PEXPIRETIME', KEYS[6]) ~= expiry
    or redis.call('ZSCORE', KEYS[1], user) or redis.call('ZSCORE', KEYS[2], user)
    or redis.call('HEXISTS', KEYS[3], user) == 1 then return { 'INCONSISTENT' } end
local raw = redis.call('GET', KEYS[7])
local ok, attempt = pcall(cjson.decode, raw or '')
if not ok or type(attempt) ~= 'table' or attempt.status ~= 'COMMITTED'
    or attempt.userId ~= user or attempt.admissionId ~= id
    or tonumber(attempt.passExpiresAt) ~= expiry
    or type(attempt.originalScore) ~= 'string' or not integer(attempt.originalScore, 9007199254740991)
    or not integer(attempt.leaseExpiresAt, 253402300799999)
    or (attempt.released ~= nil and attempt.released ~= false) then return { 'INCONSISTENT' } end
attempt.released = true
local encoded = cjson.encode(attempt)
-- First mutation may allocate. No fallible parsing follows it; deletes only free validated state.
-- KEEPTTL preserves the existing absolute deadline, including legacy persistent v4 attempts.
redis.call('SET', KEYS[7], encoded, 'KEEPTTL')
redis.call('ZREM', KEYS[4], user)
redis.call('DEL', KEYS[5], KEYS[6])
return { 'RELEASED' }