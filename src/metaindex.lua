local fakenil = {}

local function index(obj,name)
    local meta = getmetatable(obj)
    local cached = meta.cache[name]

    if cached ~= nil then
        if cached == fakenil then
            return nil
        end
        return cached

    else
        local value, isCached = get_object_member(obj, name)
        if isCached then
            if value == nil then
                meta.cache[name] = fakenil
            else
                meta.cache[name] = value
            end
        end
        return value
    end
end

return index