-- KEYS (all positions required; keys share the configured Redis Cluster slot)
-- KEYS[1] = Waiting ZSET: user -> original FIFO sequence score.
-- KEYS[2] = Processing ZSET: user -> lease expiry (Unix milliseconds).
-- KEYS[3] = Admission Records HASH: user -> reservation JSON.
-- KEYS[4] = Active Passes ZSET: user -> pass expiry (Unix milliseconds).
-- KEYS[5] = Pass Token STRING: this user's exact persisted JWT.
-- KEYS[6] = Active AdmissionId STRING: this user's current identity fence.
--
-- ARGV (required unless marked optional or mode-specific)
-- ARGV[1] = UserId: required host-authorized string.
-- ARGV[2] = Mode: required string STATUS, PASS or VALIDATE.
-- ARGV[3] = Expected AdmissionId: canonical GUID D string, required only for VALIDATE; omitted otherwise.
-- ARGV[4] = Submitted JWT: exact string, required only for VALIDATE; omitted otherwise.
-- ARGV[5] = Validated JWT expiry: integer UTC Unix milliseconds, required only for VALIDATE; omitted otherwise.
--
-- RETURNS
-- STATUS: {'ACTIVE'}; PASS: {'ACTIVE', AdmissionId, persistedJWT, expiryUnixMsText}.
-- VALIDATE: {'VALID'} or {'INACTIVE'} for a structurally live Active admission.
-- All modes may return {'WAITING'}, {'PROCESSING'}, {'NOT_FOUND'} or {'INCONSISTENT'}.
-- Read-only: STATUS returns no token bytes; expired Processing still requires Recovery.
-- An invalid mode raises a Redis error.
--
local mode = ARGV[2]
if mode ~= 'STATUS' and mode ~= 'PASS' and mode ~= 'VALIDATE' then return redis.error_reply('Invalid snapshot read mode') end
local types = { 'zset', 'zset', 'hash', 'zset', 'string', 'string' }
for i, expected in ipairs(types) do
    local actual = redis.call('TYPE', KEYS[i]).ok
    if actual ~= 'none' and actual ~= expected then return { 'INCONSISTENT' } end
end
local user = ARGV[1]
local time = redis.call('TIME')
local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
local waiting = redis.call('ZSCORE', KEYS[1], user)
local processing = redis.call('ZSCORE', KEYS[2], user)
local record = redis.call('HGET', KEYS[3], user)
local active = redis.call('ZSCORE', KEYS[4], user)
local hasToken = redis.call('EXISTS', KEYS[5]) == 1
local token = nil
if mode == 'PASS' and hasToken then token = redis.call('GET', KEYS[5]) end
local identity = redis.call('GET', KEYS[6])
local function integer(value, maximum)
    local n = tonumber(value)
    return n and n > 0 and n <= maximum and n % 1 == 0
end
local function validId(id)
    if type(id) ~= 'string' or #id ~= 36 or id ~= string.lower(id)
        or string.sub(id, 9, 9) ~= '-' or string.sub(id, 14, 14) ~= '-'
        or string.sub(id, 19, 19) ~= '-' or string.sub(id, 24, 24) ~= '-' then return false end
    local hex = string.gsub(id, '-', '')
    return #hex == 32 and string.match(hex, '^%x+$')
        and string.find('47', string.sub(id, 15, 15), 1, true)
        and string.find('89ab', string.sub(id, 20, 20), 1, true)
end
local live = active and tonumber(active) > now
if (active and not integer(active, 253402300799999))
    or (waiting and not integer(waiting, 9007199254740991)) then return { 'INCONSISTENT' } end
if live then
    if waiting or processing or record or not hasToken or redis.call('STRLEN', KEYS[5]) == 0 or not validId(identity)
        or redis.call('PEXPIRETIME', KEYS[5]) ~= tonumber(active)
        or redis.call('PEXPIRETIME', KEYS[6]) ~= tonumber(active) then return { 'INCONSISTENT' } end
    if mode == 'STATUS' then return { 'ACTIVE' } end
    if mode == 'VALIDATE' then
        if identity == ARGV[3] and tonumber(active) == tonumber(ARGV[5])
            and redis.call('GET', KEYS[5]) == ARGV[4] then return { 'VALID' } end
        return { 'INACTIVE' }
    end
    return { 'ACTIVE', identity, token, active }
end
-- An expired score alone is harmless historical residue. Orphan strings are not admission.
if hasToken or identity then return { 'INCONSISTENT' } end
if processing or record then
    if waiting or not processing or not record or not integer(processing, 253402300799999) then
        return { 'INCONSISTENT' }
    end
    local ok, value = pcall(cjson.decode, record)
    if not ok or type(value) ~= 'table' or value.userId ~= user
        or not validId(value.admissionId)
        or type(value.originalScore) ~= 'string'
        or not integer(value.originalScore, 9007199254740991) then return { 'INCONSISTENT' } end
    -- Expired leases remain Processing until Recovery resolves them; reads never recover.
    return { 'PROCESSING' }
end
if waiting then return { 'WAITING' } end
return { 'NOT_FOUND' }