local mp = require 'mp'
local utils = require 'mp.utils'
local output = os.getenv('DEMIMEDIA_SMART_FIT_PROBE')
if output then
    mp.register_event('file-loaded', function()
        mp.add_timeout(2, function()
            local state = mp.get_property_native('user-data/adaptive/fit')
            local file = assert(io.open(output, 'w'))
            file:write(utils.format_json({fit=state, zoom=mp.get_property_native('video-zoom'),
                align_x=mp.get_property_native('video-align-x'), align_y=mp.get_property_native('video-align-y')}))
            file:close()
            mp.commandv('quit')
        end)
    end)
end
