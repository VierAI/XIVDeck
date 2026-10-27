# XIVDeck (VierAI-fied) changelog

Cumulative list of changes from upstream XIVDeck. Updated as changes land.

## Features

- Hotbar keys: slots and icons are pushed to the Stream Deck on connect and whenever a hotbar changes (all 18 hotbars watched)
- Other keys (actions, macros, gearsets, classes): visible keys ask for their icons in one batch, and each icon is sent only once per connection
- Every XIVDeck key in your Stream Deck profiles is prefetched in small batches on connect (profiles are only read, rescanned every 15 s)
- Macro and gearset keys update live when their icon, name or job changes (shared macros, /micon; deleted gearsets reset to the default)
- Keys repaint when a Penumbra mod changes an icon
- Stream Deck plugin caches icons, paints empty slots transparent, skips re-sending identical images and cancels lookups for keys that scrolled away
- Redraw All Icons clears both icon caches
- Installs as its own plugin (XIVDeck.FFXIVPluginVierAIfied), imports the official settings on first start and works with the existing Stream Deck plugin

## Fixes

- The Stream Deck plugin no longer drops and reopens the connection when its settings are saved without changing host or port (double connect)
- False 'Stream Deck plugin newer than game plugin' warning caused by the fork's 4-part version
- Icon mod (Penumbra) lookups now run on the game thread

## Performance

- The once-a-second macro/gearset key check is spread over frames (about 2 ms per frame), removing a 50-80 ms hitch
- The icon cache is capped (1024 icons, least recently used dropped first) and stores each icon once
- Icons are converted to PNG once and reused, including for the older HTTP icon lookup
- Hotbar watcher (0.4.5.8): no hotbar scan at all while no Stream Deck is connected; with one connected it scans at most every 100 ms instead of every frame and no longer allocates a list per scan (a deck that connects gets the full hotbar snapshot as before)

## Other

- Own plugin icon and AI-generated code disclosure; Stream Deck plugin package renamed to match
