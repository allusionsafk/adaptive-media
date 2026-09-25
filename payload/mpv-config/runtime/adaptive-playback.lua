-- Tracks the actual video rectangle after resize, fullscreen, monitor or playlist changes.
-- The driver owns RTX activation; a configured filter is never proof of driver activity.
local mp = require 'mp'
local options = { sr = 'no', hdr = 'no', fit = 'no' }
require('mp.options').read_options(options, 'adaptive-playback')
local timer, last, failed = nil, nil, false
local function state(text)
    mp.set_property_native('user-data/adaptive/state', text)
end

-- Driver acknowledgements for this player process only. mpv logs whether the
-- NVIDIA driver accepted each VPP extension request; acceptance is recorded as
-- exactly that, and is never proof that the driver processed a frame with it.
-- Verbose messages are subscribed to only on the RTX lane.
local rtx = options.sr == 'yes' or options.hdr == 'yes'
local function driver(name, value)
    mp.set_property_native('user-data/adaptive/' .. name, value)
end
if rtx then
    mp.enable_messages('v')
    mp.register_event('log-message', function(e)
        if e.prefix ~= 'd3d11vpp' then return end
        local text = (e.text or ''):gsub('%s+$', '')
        if text:find('NVIDIA RTX Super Resolution enabled', 1, true) then
            driver('rtx-sr', 'accepted')
        elseif text:find('Failed to enable NVIDIA RTX Super Resolution', 1, true) then
            driver('rtx-sr', 'rejected: ' .. text)
        elseif text:find('NVIDIA RTX Video HDR enabled', 1, true) then
            driver('rtx-hdr', 'accepted')
        elseif text:find('Failed to enable NVIDIA RTX Video HDR', 1, true) or text:find('NVIDIA RTX Video HDR not supported', 1, true)
            or text:find('NVIDIA RTX Video HDR requested, but', 1, true) or text:find('is not supported for NVIDIA RTX Video HDR', 1, true) then
            driver('rtx-hdr', 'rejected: ' .. text)
        end
    end)
end
local function update()
    if failed then return end
    if options.sr ~= 'yes' and options.hdr ~= 'yes' then return end
    local video = mp.get_property_native('video-params')
    local osd = mp.get_property_native('osd-dimensions')
    if not video or not osd or not video.w or not video.h or video.w <= 0 or video.h <= 0 then return end
    local width = osd.w - (osd.ml or 0) - (osd.mr or 0)
    local height = osd.h - (osd.mt or 0) - (osd.mb or 0)
    if width <= 0 or height <= 0 then return end
    local aspect = video.aspect or (video.w / video.h)
    local factor = math.min(width / video.w, height / video.h)
    local sr = options.sr == 'yes' and factor > 1.001 and factor <= 8 and math.abs(aspect - video.w / video.h) < 0.015
    local sdr = { ['bt.1886']=true, srgb=true, ['gamma1.8']=true, ['gamma2.0']=true, ['gamma2.2']=true, ['gamma2.4']=true, ['gamma2.6']=true, ['gamma2.8']=true, linear=true }
    local hdr = options.hdr == 'yes' and sdr[video.gamma] == true
    local key = (sr and string.format('%.4f', factor) or 'native') .. (hdr and '-hdr' or '')
    if last == key then return end
    last = key
    local filters = mp.get_property_native('vf') or {}
    local next_filters = {}
    for _, filter in ipairs(filters) do
        if filter.label ~= 'adaptive-vpp' then table.insert(next_filters, filter) end
    end
    if sr or hdr then
        local params = {}
        if sr then params.scale = string.format('%.6f', factor); params['scaling-mode'] = 'nvidia' end
        if hdr then params['nvidia-true-hdr'] = 'yes' end
        table.insert(next_filters, {name='d3d11vpp', label='adaptive-vpp', enabled=true, params=params})
    end
    local ok, error = mp.set_property_native('vf', next_filters)
    if not ok then
        failed = true
        mp.commandv('vf', 'remove', '@adaptive-vpp')
        state('RTX filter could not be applied; continuing with standard scaling.')
        mp.osd_message('RTX processing unavailable. Standard scaling remains available.', 5)
    elseif sr then
        state(string.format('RTX VPP configured for the observed video rectangle (%d × %d). Driver activation is unverified.', width, height))
    else
        state(hdr and 'RTX HDR filter configured; driver activation is unverified.' or 'RTX SR unnecessary at the current window size.')
    end
end
local function schedule()
    if timer then timer:kill() end
    timer = mp.add_timeout(0.35, update)
end
mp.observe_property('osd-dimensions', 'native', schedule)
mp.observe_property('video-params', 'native', schedule)
-- Each loaded file is a new source: delivery evidence restarts from it, and an
-- acknowledgement for the previous file's filter says nothing about this one.
local epoch = 0
mp.register_event('file-loaded', function()
    last = nil; failed = false
    epoch = epoch + 1
    mp.set_property_native('user-data/adaptive/source-epoch', epoch)
    if rtx then driver('rtx-sr', 'pending'); driver('rtx-hdr', 'pending') end
    schedule()
end)

-- Keep the established measured motion guard; show its effective fallback in playback.
local had_interpolation = mp.get_property_native('interpolation', false)
mp.observe_property('interpolation', 'bool', function(_, enabled)
    if had_interpolation and enabled == false then
        mp.osd_message('Smooth motion disabled because display timing became unstable.', 6)
    end
    had_interpolation = enabled
end)

-- Smart Fill is an explicit, source-only crop. Screenshot sampling uses mpv's
-- in-memory video frame (no subtitle/OSD and no scratch file). Renderer zoom
-- and alignment display only existing pixels. No fit state is called active
-- until a frame has actually been sampled and the renderer properties accept
-- the requested geometry.
if options.fit == 'yes' then
    local grid_w, grid_h = 64, 36
    local previous, aligned, sample_count, cuts = nil, 0, 0, 0
    local stopped = false
    local function report(value)
        mp.set_property_native('user-data/adaptive/fit', value)
    end
    local function original(reason)
        mp.set_property_number('video-zoom', 0)
        mp.set_property_number('video-align-x', 0)
        mp.set_property_number('video-align-y', 0)
        report({state='fallback', reason=reason, samples=sample_count})
        stopped = true
    end
    local function sample()
        if stopped or mp.get_property_native('pause') then return end
        local video = mp.get_property_native('video-params')
        local osd = mp.get_property_native('osd-dimensions')
        if not video or not osd or not video.w or not video.h or not osd.w or not osd.h
            or video.w <= 0 or video.h <= 0 or osd.w <= 0 or osd.h <= 0 then return end
        local source_aspect = video.aspect or video.w / video.h
        local screen_aspect = osd.w / osd.h
        local ratio = math.max(source_aspect / screen_aspect, screen_aspect / source_aspect)
        if math.abs(source_aspect - video.w / video.h) >= 0.015 or ratio > 1.4 then
            original('source aspect or crop geometry is unsafe')
            return
        end
        if ratio < 1.03 then
            mp.set_property_number('video-zoom', 0)
            mp.set_property_number('video-align-x', 0)
            mp.set_property_number('video-align-y', 0)
            report({state='not-needed', reason='source already fits the current window', samples=sample_count})
            return
        end
        local begun = mp.get_time()
        local ok, frame = pcall(mp.command_native, {'screenshot-raw', 'video', 'bgr0'})
        if not ok or type(frame) ~= 'table' or type(frame.data) ~= 'string' or
            frame.format ~= 'bgr0' or frame.w ~= video.w or frame.h ~= video.h or
            type(frame.stride) ~= 'number' or frame.stride < frame.w * 4 or
            #frame.data < frame.stride * frame.h then
            original('source frame capture unavailable')
            return
        end
        local luma, scores = {}, {}
        local movement, total = 0, 0
        local wide = source_aspect > screen_aspect
        for gy = 1, grid_h do
            local y = math.min(frame.h - 1, math.floor((gy - 0.5) * frame.h / grid_h))
            for gx = 1, grid_w do
                local x = math.min(frame.w - 1, math.floor((gx - 0.5) * frame.w / grid_w))
                local offset = y * frame.stride + x * 4 + 1
                local b, g, r = frame.data:byte(offset, offset + 2)
                local value = (r * 54 + g * 183 + b * 19) / 256
                local index = (gy - 1) * grid_w + gx
                luma[index] = value
                local motion = previous and math.abs(value - previous[index]) or 0
                movement = movement + motion
                local edge = gx > 1 and math.abs(value - luma[index - 1]) or 0
                if gy > 1 then edge = edge + math.abs(value - luma[index - grid_w]) end
                local focus = wide and (1 - 0.6 * math.abs((gy - 0.5) / grid_h - 0.5)) or 1
                local salience = (edge + 0.7 * motion) * focus
                local axis = wide and gx or gy
                scores[axis] = (scores[axis] or 0) + salience
                total = total + salience
            end
        end
        local shot_change = previous and movement / (grid_w * grid_h) > 42
        if shot_change then cuts = cuts + 1; aligned = 0 end
        previous = luma
        local length = wide and grid_w or grid_h
        local crop = math.max(1, math.min(length, math.floor(length / ratio + 0.5)))
        local range = length - crop
        local center_left = range / 2
        local best_left, best_score = center_left, -math.huge
        if not shot_change and total > 1 then
            for left = 0, range do
                local score = 0
                for axis = left + 1, left + crop do score = score + (scores[axis] or 0) end
                -- A weak saliency difference should not dislodge the authored centre.
                score = score - math.abs(left - center_left) * total * 0.015 / math.max(1, range)
                if score > best_score then best_score = score; best_left = left end
            end
            local center_score = 0
            local center_int = math.floor(center_left + 0.5)
            for axis = center_int + 1, center_int + crop do center_score = center_score + (scores[axis] or 0) end
            if best_score < center_score * 1.04 then best_left = center_left end
        end
        local wanted = range > 0 and 2 * (best_left - center_left) / range or 0
        if math.abs(wanted - aligned) > 0.04 then
            local step = math.max(-0.12, math.min(0.12, (wanted - aligned) * 0.22))
            aligned = math.max(-1, math.min(1, aligned + step))
        end
        local zoom = math.log(ratio) / math.log(2)
        local set_zoom = mp.set_property_number('video-zoom', zoom)
        local set_axis = mp.set_property_number(wide and 'video-align-x' or 'video-align-y', aligned)
        mp.set_property_number(wide and 'video-align-y' or 'video-align-x', 0)
        if not set_zoom or not set_axis then
            original('renderer rejected Smart Fill geometry')
            return
        end
        sample_count = sample_count + 1
        report({state='active', samples=sample_count, zoom=zoom, align=aligned, cuts=cuts,
            capture_ms=math.floor((mp.get_time() - begun) * 1000 + 0.5)})
    end
    mp.register_event('file-loaded', function()
        previous, aligned, sample_count, cuts, stopped = nil, 0, 0, 0, false
        report({state='pending', samples=0})
        mp.set_property_number('video-zoom', 0)
        mp.set_property_number('video-align-x', 0)
        mp.set_property_number('video-align-y', 0)
    end)
    mp.add_periodic_timer(0.75, sample)
end

