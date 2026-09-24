-- DemiMedia "Playback details": a right-side panel drawn inside mpv.
--
-- Toggle: script-binding demimedia_details/toggle (Tab in input.conf, the
-- OSC's "Playback details" button, or a click on the OSC title).
--
-- Two kinds of rows, never mixed:
--   * Source, Requested, Planned, Health, Fallback and Recovery come from
--     DemiMedia. The app sets the persistent property user-data/demimedia/plan
--     (PlayerDetailsPayload.cs), so a value set before this script loaded is
--     not lost; script-message-to demimedia_details set-plan <json> also works.
--   * Observed is read here, live, from mpv properties only. A requested value
--     is never shown as observed, and a driver acknowledgement is shown as
--     exactly that (see runtime/adaptive-playback.lua).
--
-- Cost: nothing is polled or drawn while hidden (the overlay is removed).
-- While visible it refreshes once a second, redraws on resize or new data, and
-- resubmits the overlay only when its text actually changed.
local mp = require 'mp'
local msg = require 'mp.msg'
local utils = require 'mp.utils'
local assdraw = require 'mp.assdraw'

local opts = {
    width = 380,            -- panel width in px at a 720 px tall window; scales with height
    font = '',              -- empty: --osd-font (Segoe UI in DemiMedia's mpv.conf)
    mono_font = 'Consolas', -- machine values: codecs, decoder, renderer, sizes
    font_size = 15,         -- ASS size at 720 px (ASS sizes run about 1.33x CSS px)
    min_fit = 0.8,          -- smallest text scale used to fit a long panel before rows are cut
    background_alpha = 8,   -- 0 opaque .. 255 transparent
    bar_reserve = 56,       -- OSC bottombar height at 720 px; content stays above it
}
require('mp.options').read_options(opts, 'demimedia-details')

-- DemiMedia tokens (RRGGBB).
local C = {
    panel = '1B2226', edge = '384247', rule = '364047',
    title = 'E4E8E9', text = 'E4E8E9', value = 'E6EAEB', label = 'AEB9BD',
    muted = 'AAB5B9', caps = 'A2B0B4',
    alert_back = '2D2927', alert_edge = '885D49', alert_text = 'F2E5DC',
}
-- UTF-8 as bytes so this file stays ASCII.
local TIMES, DOT, ELLIPSIS = '\195\151', ' \194\183 ', '\226\128\166'

local overlay = mp.create_osd_overlay('ass-events')
overlay.z = 500             -- above video and subtitles; OSC draws at 1000, menus at 2000
local visible = false
local plan = nil
local ticker, pending = nil, nil
local submitted = nil
local warned = false

local function bgr(hex) return hex:sub(5, 6) .. hex:sub(3, 4) .. hex:sub(1, 2) end

local function esc(s)
    s = tostring(s)
    s = s:gsub('\\', '\\\239\187\191')   -- a zero-width no-break space defuses ASS escapes
    s = s:gsub('{', '\\{')
    s = s:gsub('}', '\\}')
    s = s:gsub('[\r\n]+', ' ')
    return s
end

local function ulen(s)
    local _, n = tostring(s):gsub('[^\128-\191]', '')
    return n
end

local function clip(s, max)
    s = tostring(s)
    if ulen(s) <= max then return s end
    local out, n = {}, 0
    for ch in s:gmatch('[\1-\127\194-\244][\128-\191]*') do
        if n >= math.max(1, max - 1) then break end
        n = n + 1
        out[n] = ch
    end
    return table.concat(out) .. ELLIPSIS
end

local function wrap(text, max)
    local lines, line = {}, ''
    for word in tostring(text):gmatch('%S+') do
        if line ~= '' and ulen(line) + 1 + ulen(word) > max then
            lines[#lines + 1] = line
            line = word
        else
            line = (line == '') and word or (line .. ' ' .. word)
        end
    end
    if line ~= '' then lines[#lines + 1] = line end
    for i, l in ipairs(lines) do lines[i] = clip(l, max) end
    return lines
end

local function str(v) return (type(v) == 'string' and v ~= '') and v or nil end
local function list(v) return type(v) == 'table' and v or {} end

--------------------------------------------------------------------------------
-- Observed: live mpv properties only.

local context_api = { winvk = 'vulkan', d3d11 = 'd3d11', angle = 'angle', win = 'opengl', dxinterop = 'opengl' }

local function size(t)
    if type(t) ~= 'table' or type(t.w) ~= 'number' or type(t.h) ~= 'number' or t.w <= 0 or t.h <= 0 then return nil end
    return string.format('%d %s %d', t.w, TIMES, t.h)
end

local function hz(v)
    if type(v) ~= 'number' or v <= 0 then return nil end
    local r = math.floor(v + 0.5)
    if math.abs(v - r) < 0.005 then return string.format('%d Hz', r) end
    return string.format('%.3f Hz', v)
end

-- user-data/adaptive/rtx-* records what the driver said to a request, never
-- that it processed a frame (adaptive-playback.lua), and is worded as such.
local function driver(v)
    v = str(v)
    if not v then return nil end
    if v == 'accepted' then return 'Driver accepted' end
    if v == 'pending' then return 'Waiting for driver' end
    if v:find('^rejected') then return 'Driver rejected' end
    return v
end

local function observed()
    local rows = {}
    local function add(label, value, mono)
        if value then rows[#rows + 1] = { label, value, mono } end
    end
    local hw = mp.get_property_native('hwdec-current')
    if type(hw) == 'string' then
        local soft = hw == '' or hw == 'no'
        add('Decoder', soft and 'Software' or hw, not soft)
    end
    local vo = str(mp.get_property_native('current-vo'))
    if vo then
        local ctx = str(mp.get_property_native('current-gpu-context'))
        add('Renderer', vo .. (ctx and (DOT .. (context_api[ctx] or ctx)) or ''), true)
    end
    local vp = mp.get_property_native('video-params')
    local vsize = size(vp)
    if vsize then add('Video', vsize .. (str(vp.gamma) and (DOT .. vp.gamma) or ''), true) end
    local osize = size(mp.get_property_native('osd-dimensions'))
    if osize and vsize then
        local target = mp.get_property_native('video-target-params')
        local gamma = type(target) == 'table' and str(target.gamma) or nil
        local range = gamma and ((gamma == 'pq' or gamma == 'hlg') and ('HDR (' .. gamma:upper() .. ')') or 'SDR') or nil
        add('Output', osize .. (range and (DOT .. range) or ''), true)
    end
    add('Display', hz(mp.get_property_native('display-fps')) or hz(mp.get_property_native('estimated-display-fps')), true)
    local sync = str(mp.get_property_native('video-sync'))
    if sync then
        local interp = mp.get_property_native('interpolation') == true
        local active = mp.get_property_native('display-sync-active') == true
        add('Video sync', sync .. (interp and (active and ', interpolating' or ', interpolation idle') or ''), true)
    end
    local drops = mp.get_property_native('frame-drop-count')
    if type(drops) == 'number' then
        local dec = mp.get_property_native('decoder-frame-drop-count')
        add('Dropped frames', tostring(drops) .. ((type(dec) == 'number' and dec > 0) and (DOT .. dec .. ' by decoder') or ''), true)
    end
    local at = mp.get_property_native('current-tracks/audio')
    if type(at) == 'table' then
        local parts = {}
        if str(at.codec) then parts[#parts + 1] = at.codec end
        if type(at['demux-channel-count']) == 'number' then parts[#parts + 1] = at['demux-channel-count'] .. ' ch' end
        if str(at.lang) then parts[#parts + 1] = at.lang end
        if #parts > 0 then add('Audio', table.concat(parts, DOT), true) end
    end
    add('RTX Super Resolution', driver(mp.get_property_native('user-data/adaptive/rtx-sr')))
    add('RTX Video HDR', driver(mp.get_property_native('user-data/adaptive/rtx-hdr')))
    return rows, str(mp.get_property_native('user-data/adaptive/state'))
end

--------------------------------------------------------------------------------
-- Layout. Every block is measured first, so a long panel can shrink its text
-- (to opts.min_fit) and then cut from the end instead of running under the bar.

local function build(s, f, inner)
    local k = s * f
    local fs = opts.font_size * k
    local cw, mw = fs * 0.46, fs * 0.52       -- deliberately generous glyph advances
    local blocks = {}
    local function block(h, draw) blocks[#blocks + 1] = { h = h, draw = draw } end

    local T = {}   -- draw primitives, bound at render time
    local function caps(name, color, first)
        local gap = first and 4 * k or 16 * k
        block(gap + 20 * k, function(y, x0, x1, pad)
            T.text(x0 + pad, y + gap + 10 * k, 4, fs * 0.84, color or C.caps, name:upper(), { bold = true, spacing = 0.9 * k })
        end)
    end
    local function kv(label, value, mono, first, muted)
        local label_w = ulen(label) * cw
        local max = math.max(4, math.floor((inner - label_w - 14 * k) / (mono and mw or cw)))
        local shown = clip(value, max)
        local h = 25 * k
        block(h, function(y, x0, x1, pad)
            if not first then T.rect(x0 + pad, y, x1 - pad, y + math.max(1, math.floor(s + 0.5)), C.rule) end
            T.text(x0 + pad, y + h / 2, 4, fs, C.label, label)
            T.text(x1 - pad, y + h / 2, 6, mono and fs * 0.94 or fs, muted and C.muted or C.value, shown,
                { bold = not muted, font = mono and opts.mono_font or nil })
        end)
    end
    local function lines(text, size, color, bold, lead, indent)
        indent = indent or 0
        local wrapped = wrap(text, math.max(8, math.floor((inner - indent) / (cw * size / fs))))
        local lh = lead * k
        block(#wrapped * lh, function(y, x0, x1, pad)
            for i, l in ipairs(wrapped) do
                T.text(x0 + pad + indent, y + (i - 0.5) * lh, 4, size, color, l, { bold = bold })
            end
        end)
    end

    block(34 * k, function(y, x0, x1, pad)
        T.text(x0 + pad, y + 15 * k, 4, 22 * k, C.title, 'Playback details', { bold = true })
    end)

    local p = type(plan) == 'table' and plan or nil
    local health = p and type(p.health) == 'table' and str(p.health.state) and p.health or nil
    local fallback, recovery = list(p and p.fallback), list(p and p.recovery)
    local alert = (health and health.attention == true) or #fallback > 0 or #recovery > 0

    -- Something changed: say so first, in the attention colours, and nowhere else.
    if alert then
        local cpad = 12 * k
        local card_inner = inner - 2 * cpad
        local sub = {}
        local function cline(text, size, bold, lead, color)
            for _, l in ipairs(wrap(text, math.max(8, math.floor(card_inner / (cw * size / fs))))) do
                sub[#sub + 1] = { l, size, bold, lead * k, color or C.alert_text }
            end
        end
        cline((#fallback > 0 or #recovery > 0) and 'Playback changed path' or 'Playback needs attention', fs, true, 22)
        if health and health.attention == true then
            cline(health.state, fs * 0.94, false, 19)
            for _, l in ipairs(list(health.lines)) do if str(l) then cline(l, fs * 0.88, false, 18) end end
        end
        for _, l in ipairs(fallback) do if str(l) then cline(l, fs * 0.88, false, 18) end end
        for _, l in ipairs(recovery) do if str(l) then cline('Recovery: ' .. l, fs * 0.88, false, 18) end end
        local ch = 2 * 9 * k
        for _, l in ipairs(sub) do ch = ch + l[4] end
        block(10 * k + ch, function(y, x0, x1, pad)
            local top = y + 10 * k
            T.rect(x0 + pad, top, x1 - pad, top + ch, C.alert_back, 0, 6 * k, C.alert_edge)
            local yy = top + 9 * k
            for _, l in ipairs(sub) do
                T.text(x0 + pad + cpad, yy + l[4] / 2, 4, l[2], l[5], l[1], { bold = l[3] })
                yy = yy + l[4]
            end
        end)
    end

    if not p then
        caps('Plan', nil, not alert)
        lines('Waiting for DemiMedia' .. ELLIPSIS, fs * 0.94, C.muted, false, 20)
    else
        local first = not alert
        if #list(p.source) > 0 then
            caps('Source', nil, first); first = false
            for i, r in ipairs(p.source) do
                if type(r) == 'table' and str(r[1]) and str(r[2]) then
                    local unknown = r[2] == 'Unknown'
                    kv(r[1], r[2], not unknown and (r[1] == 'Codec' or r[1] == 'Audio'), i == 1, unknown)
                end
            end
        end
        for _, section in ipairs({ { 'Requested', p.requested }, { 'Planned', p.planned } }) do
            if #list(section[2]) > 0 then
                caps(section[1], nil, first); first = false
                for _, l in ipairs(section[2]) do
                    if str(l) then lines(l, fs * 0.94, C.text, false, 20) end
                end
            end
        end
    end

    local rows, state = observed()
    caps('Observed', nil, false)
    if #rows == 0 then lines('Nothing reported yet', fs * 0.94, C.muted, false, 20) end
    for i, r in ipairs(rows) do kv(r[1], r[2], r[3], i == 1) end
    if state then lines(state, fs * 0.84, C.muted, false, 17) end

    -- Healthy playback stays quiet: plain words, no colour, no badge.
    if health and health.attention ~= true then
        caps('Health', nil, false)
        lines(health.state, fs * 0.94, C.text, false, 20)
        for _, l in ipairs(list(health.lines)) do if str(l) then lines(l, fs * 0.84, C.muted, false, 17) end end
    end

    local total = 0
    for _, b in ipairs(blocks) do total = total + b.h end
    return blocks, total, T
end

local function compose(w, h)
    local s = h / 720
    local pw = math.floor(math.min(opts.width * s, w * 0.5) + 0.5)
    local x0, x1 = w - pw, w
    local pad = 20 * s
    local inner = pw - 2 * pad
    local top = 16 * s
    local bottom = h - opts.bar_reserve * s
    local footer_y = bottom - 16 * s
    local avail = footer_y - 14 * s - top
    local font = opts.font ~= '' and opts.font or (str(mp.get_property('options/osd-font')) or 'sans-serif')

    local blocks, total, T = build(s, 1, inner)
    if total > avail then
        blocks, total, T = build(s, math.max(opts.min_fit, avail / total), inner)
    end

    local ass = assdraw.ass_new()
    function T.rect(ax, ay, bx, by, color, alpha, radius, edge)
        ass:new_event()
        ass:append(string.format('{\\an7\\pos(0,0)\\shad0\\blur0\\1c&H%s&\\1a&H%02X&%s}', bgr(color), alpha or 0,
            edge and string.format('\\bord%.2f\\3c&H%s&', math.max(1, s), bgr(edge)) or '\\bord0'))
        ass:draw_start()
        if radius and radius > 0 then ass:round_rect_cw(ax, ay, bx, by, radius) else ass:rect_cw(ax, ay, bx, by) end
        ass:draw_stop()
    end
    function T.text(x, y, an, fsize, color, text, o)
        o = o or {}
        ass:new_event()
        ass:append(string.format('{\\an%d\\pos(%.1f,%.1f)\\fn%s\\fs%.2f\\bord0\\shad0\\blur0\\1c&H%s&\\b%d\\fsp%.2f\\q2}',
            an, x, y, o.font or font, fsize, bgr(color), o.bold and 1 or 0, o.spacing or 0) .. esc(text))
    end

    -- The surface runs the full height; the OSC bar draws over its foot.
    T.rect(x0, 0, x1, h, C.panel, opts.background_alpha)
    T.rect(x0, 0, x0 + math.max(1, math.floor(s + 0.5)), h, C.edge)
    local y = top
    for i, b in ipairs(blocks) do
        if y + b.h > footer_y - 10 * s then
            T.text(x0 + pad, y + 10 * s, 4, opts.font_size * s * 0.9, C.muted, ELLIPSIS)
            break
        end
        b.draw(y, x0, x1, pad)
        y = y + b.h
    end
    T.text(x0 + pad, footer_y, 4, opts.font_size * s * 0.84, C.muted, 'Tab to close')
    return ass.text
end

--------------------------------------------------------------------------------

local function render()
    if not visible then
        if submitted then overlay:remove(); submitted = nil end
        return
    end
    local w, h = mp.get_osd_size()
    if not w or w <= 0 or not h or h <= 0 then return end
    local ok, data = pcall(compose, w, h)
    if not ok then
        -- Never let a surprising property or payload take the panel (or its timer) down.
        if not warned then msg.warn('details panel: ' .. tostring(data)); warned = true end
        return
    end
    local key = w .. 'x' .. h .. '\n' .. data
    if key == submitted then return end
    overlay.res_x, overlay.res_y = w, h
    overlay.data = data
    overlay:update()
    submitted = key
end

-- Coalesce bursts (a window drag, a new payload) into one redraw.
local function soon()
    if not visible or pending then return end
    pending = mp.add_timeout(0.05, function() pending = nil; render() end)
end

local function set_visible(v)
    visible = v
    if ticker then ticker:kill(); ticker = nil end
    if pending then pending:kill(); pending = nil end
    if visible then ticker = mp.add_periodic_timer(1, render) end
    render()
end

local function accept(value)
    if type(value) == 'table' then plan = value; soon() end
end

mp.add_key_binding(nil, 'toggle', function() set_visible(not visible) end)
mp.register_script_message('show', function() set_visible(true) end)
mp.register_script_message('hide', function() set_visible(false) end)
mp.register_script_message('set-plan', function(json) accept(utils.parse_json(json or '')) end)
mp.observe_property('user-data/demimedia/plan', 'native', function(_, value) accept(value) end)
mp.observe_property('osd-dimensions', 'native', soon)
