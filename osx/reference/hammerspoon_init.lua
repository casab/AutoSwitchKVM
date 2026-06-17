-- KVM-driven Bluetooth handoff for Apple Magic Trackpad 3 (macOS, non-blocking)
-- Watches specific USB hubs; pairs/connects on Selected, disconnects/unpairs on Unselected.
-- =======================
-- Config
-- =======================
local BT_MAC = "3C-50-02-BF-22-45"    -- blueutil accepts ':' or '-'
local KVM_VENDOR = 0x05E3
local KVM_PRODS  = { [0x0626] = true, [0x0610] = true }
local DEBOUNCE_MS        = 1200
local CONNECT_RETRY_MAX  = 6
local CONNECT_RETRY_SECS = 5
local BLUE_TIMEOUT_SECS  = 5           -- per blueutil call timeout
local SHOW_NOTIFICATIONS = false
-- =======================
-- Utilities & logging
-- =======================
local function notify(title, text)
  if SHOW_NOTIFICATIONS then
    hs.notify.new({ title = title, informativeText = text }):send()
  end
end
local function printf(fmt, ...)
  hs.printf("[KVM-BT] " .. fmt, ...)
end
local function fileExists(path)
  return hs.fs.attributes(path) ~= nil
end
-- Resolve blueutil path
local BLUEUTIL = "blueutil"
if fileExists("/opt/homebrew/bin/blueutil") then
  BLUEUTIL = "/opt/homebrew/bin/blueutil"
elseif fileExists("/usr/local/bin/blueutil") then
  BLUEUTIL = "/usr/local/bin/blueutil"
end
-- =======================
-- Non-blocking blueutil: serialized task queue with timeouts
-- =======================
local blueQ = {}
local blueBusy = false
local function runBlueAsync(args, onDone, timeoutSecs)
  table.insert(blueQ, { args = args, onDone = onDone, timeout = timeoutSecs or BLUE_TIMEOUT_SECS })
  if not blueBusy then
    blueBusy = true
    local function pump()
      local item = table.remove(blueQ, 1)
      if not item then blueBusy = false; return end
      local done = false
      local killer
      local t = hs.task.new(BLUEUTIL,
        function(exitCode, stdOut, stdErr)
          if done then return end
          done = true
          if killer then killer:stop() end
          -- normalize
          exitCode = exitCode or 0
          stdOut = stdOut or ""
          stdErr = stdErr or ""
          if item.onDone then item.onDone(exitCode, stdOut, stdErr) end
          pump() -- next
        end,
        item.args
      )
      killer = hs.timer.doAfter(item.timeout, function()
        if done then return end
        printf("blueutil timeout after %ds: %s %s", item.timeout, BLUEUTIL, table.concat(item.args, " "))
        t:terminate()
        done = true
        if item.onDone then item.onDone(124, "", "timeout") end
        pump()
      end)
      if not t:start() then
        if killer then killer:stop() end
        done = true
        printf("failed to start blueutil task: %s", table.concat(item.args, " "))
        if item.onDone then item.onDone(127, "", "spawn-failed") end
        pump()
      end
    end
    pump()
  end
end
local function blueIsConnected(cb)
  runBlueAsync({"--is-connected", BT_MAC}, function(rc, out)
    -- rc is 0 even for "0"/"1"; rely on stdout
    local s = tostring(out or ""):gsub("%s+", "")
    cb(s == "1")
  end)
end
local function blueConnect(cb)   runBlueAsync({"--connect",  BT_MAC}, function() if cb then cb() end end) end
local function blueDisconnect(cb)runBlueAsync({"--disconnect",BT_MAC}, function() if cb then cb() end end) end
local function bluePair(cb)      runBlueAsync({"--pair",     BT_MAC}, function() if cb then cb() end end) end
local function blueUnpair(cb)    runBlueAsync({"--unpair",   BT_MAC}, function() if cb then cb() end end) end
local function bluePowerOn(cb)   runBlueAsync({"--power",    "1"},    function() if cb then cb() end end) end
-- =======================
-- State
-- =======================
local hubsPresent    = {}
local selected       = false
local lastReason     = "startup"
local connectTimer   = nil
local debounceTimer  = nil
local connectRunning = false  -- guard to avoid overlapping attempts
local function stopConnectTimer()
  if connectTimer then connectTimer:stop(); connectTimer = nil end
end
-- =======================
-- Actions
-- =======================
local function doDisconnect(reason)
  stopConnectTimer()
  connectRunning = false
  printf("Unselected: disconnect/unpair (reason: %s)", reason or "unknown")
  -- Order: disconnect first, then unpair (best-effort)
  blueDisconnect(function()
    blueUnpair(function()
      notify("KVM BT", "Trackpad disconnected/unpaired")
    end)
  end)
end
local function doConnect()
  if connectRunning then return end
  connectRunning = true
  stopConnectTimer()
  local attempts = 0
  local function finish(msg, note)
    printf(msg)
    if note then notify("KVM BT", note) end
    stopConnectTimer()
    connectRunning = false
  end
  local function scheduleRetry(attempt)
    -- one-shot; no repeating timer, no manual re-arm races
    connectTimer = hs.timer.doAfter(CONNECT_RETRY_SECS, attempt)
  end
  local function attempt()
    if not selected then finish("Connect aborted: host no longer selected."); return end
    blueIsConnected(function(already)
      if already then finish("Already connected to Trackpad.", "Already connected"); return end
      attempts = attempts + 1
      printf("Connect attempt %d/%d …", attempts, CONNECT_RETRY_MAX)
      bluePowerOn(function()
        -- Bond was removed on leave (verified via blueutil --paired), and
        -- Windows may have re-paired since, so re-pair before connecting.
        bluePair(function()
          blueConnect(function()
            blueIsConnected(function(ok)
              if ok then finish("Connected to Trackpad.", "Trackpad connected"); return end
              if not selected then finish("Connect aborted mid-flight: not selected."); return end
              if attempts >= CONNECT_RETRY_MAX then
                finish(string.format("Connect gave up after %d attempts.", attempts), "Connect attempts ended")
                return
              end
              scheduleRetry(attempt)   -- single-shot next try
            end)
          end)
        end)
      end)
    end)
  end
  -- kick off quickly
  connectTimer = hs.timer.doAfter(0.1, attempt)
end
local function evaluateSelection()
  local isPresent = (hubsPresent[0x0626] or hubsPresent[0x0610]) and true or false
  if isPresent and not selected then
    selected = true
    printf("Selected (KVM hub present) — %s", lastReason)
    notify("KVM BT", "Selected via KVM; connecting …")
    doConnect()
  elseif (not isPresent) and selected then
    selected = false
    printf("Unselected (KVM hubs absent) — %s", lastReason)
    doDisconnect(lastReason)
  end
end
local function scheduleEvaluate()
  if not debounceTimer then
    debounceTimer = hs.timer.delayed.new(DEBOUNCE_MS / 1000.0, evaluateSelection)
  end
  debounceTimer:stop(); debounceTimer:start()
end
-- =======================
-- USB Watcher
-- =======================
local function usbEvent(e)
  if e and e.vendorID == KVM_VENDOR and KVM_PRODS[e.productID] then
    if e.eventType == "added" then
      hubsPresent[e.productID] = true
      lastReason = string.format("Selected via KVM hub arrival (0x%04X)", e.productID)
    elseif e.eventType == "removed" then
      hubsPresent[e.productID] = nil
      lastReason = string.format("Unselected via KVM hub removal (0x%04X)", e.productID)
    end
    scheduleEvaluate()
  end
end
local watcher = hs.usb.watcher.new(usbEvent)
-- =======================
-- Sleep/Wake robustness
-- =======================
local function seedAttached()
  hubsPresent = {}
  for _,d in ipairs(hs.usb.attachedDevices()) do
    if d.vendorID == KVM_VENDOR and KVM_PRODS[d.productID] then
      hubsPresent[d.productID] = true
    end
  end
  lastReason = "initial scan"
  scheduleEvaluate()
end
local function onCaffeinate(event)
  if event == hs.caffeinate.watcher.systemWillSleep then
    -- behave like unselected so the other OS can take it while Mac sleeps
    if selected then
      printf("System sleep → proactive disconnect/unpair")
      doDisconnect("sleep")
    end
  elseif event == hs.caffeinate.watcher.systemDidWake
      or event == hs.caffeinate.watcher.screensDidWake then
    -- refresh hub presence & try again if selected
    seedAttached()
  end
end
local cafWatcher = hs.caffeinate.watcher.new(onCaffeinate)
-- Auto dock hide script
local function setDockAutohide(flag)
  hs.task.new("/usr/bin/defaults", nil, {"write","com.apple.dock","autohide","-bool", flag and "true" or "false"}):start()
  hs.task.new("/usr/bin/killall", nil, {"Dock"}):start()
end
local function updateDock()
  if #hs.screen.allScreens() > 1 then
    setDockAutohide(false)   -- external connected: show Dock
  else
    setDockAutohide(true)    -- laptop only: auto-hide Dock
  end
end
-- =======================
-- Start
-- =======================
hs.screen.watcher.new(updateDock):start()
updateDock()
seedAttached()
watcher:start()
cafWatcher:start()
printf("KVM-BT handoff loaded. Watching hubs 0x05E3:{0x0626,0x0610} for Trackpad %s (blueutil=%s)", BT_MAC, BLUEUTIL)
