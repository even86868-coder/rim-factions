# Rim Factions - Multiplayer Co-op

RimWorld multiplayer co-op with up to 4 friends on one planet, without the constant desyncs. Each of you plays your own
colony in your own save; Rim Factions links your games so gifts, colonists, help and raids travel between
them. When you want to play side by side, a live shared session opens just for that, then everyone goes
back to their own game.

## What you get
- **Your own colony, a shared planet.** Every player runs a normal solo save, so nothing has to stay in
  lockstep and nothing desyncs. Friends' colonies show up on your world map. Starting a new colony from the
  Rim Factions menu uses your friend's planet seed and settings.
- **One-click join links.** Friends who join from your link get a welcome screen: join your colony live,
  start their own colony on your planet, or keep playing one of their saves.
- **Call for help.** Under attack? The *Call friends for help* alert asks every online friend; one click on
  their letter sends their best fighters to fight beside you.
- **Trade caravans.** Send a caravan to a friend's colony and pick *Trade with*: their real stockpile and
  home-area items open in the normal trade screen. Forbidden items stay private.
- **Planet-wide assaults.** Every few days mechanoids or raiders hit every online colony at once; colonies
  that help another earn 300 silver.
- **Gifts by drop pod.** Select items (or silver) on your home map and press *Gift to colony*, or gift a
  colonist for good. Friends who are offline get them when they're back.
- **Trips.** Select colonists and press *Send to colony*:
  - **Help (AI):** they drop into your friend's colony as allies, stay to fight, and come home when it's
    calm, with their wounds and gear.
  - **Raid (AI):** they walk in as hostile raiders. Survivors who flee come home; downed raiders can be
    captured.
  - **Help live:** your friend accepts, their game becomes a live session, your game saves and you join to
    control your colonists next to them. *Send home* ends it: survivors return to your save and both games
    go back to solo.
- **Start together, split later.** The host presses *Invite friends to this colony*; friends press *Join
  live*. Everyone gets 3 colonists of their own on one map. When you're ready, *Leave to found own colony*
  takes your colonists away as a caravan on the same planet, and your game becomes your own save.
- **Goodwill news feed.** Everyone gets a letter when colonies gift, help, raid, split off or change
  relations. Factions start neutral; gifts and help raise goodwill, raids lower it; at -75 you're hostile.
- **Keeps your mods.** Your own mod list stays on. Solo play and AI trips work with any mods; live sessions
  use the RimWorld Multiplayer mod plus its Compatibility pack (mods not made for multiplayer can still
  desync there, and only there).

## Playing
- Press Play: you host a planet. Friends join with your Melty join link (it carries your Steam ID; Steam
  relays the connection, no port forwarding).
- Open the **Rim Factions** button (bottom bar in game, or on the main menu) for players, goodwill and the
  play-together buttons.

## Requirements
RimWorld 1.6, Steam running. Up to 4 players per planet.

## Files
- `Mods/RimFactions` – this mod.
- `Mods/Multiplayer`, `Mods/MultiplayerCompatibility`, `Mods/Prepatcher` – bundled, unchanged, MIT (see CREDITS.md).
- `Launcher/RimFactionsLauncher.exe` – adds these to your mod list in the right order (your
  ModsConfig.xml is backed up once as ModsConfig.before-RimFactions.xml) and starts RimWorld.

## Testing builds (before Melty's review)
Download [RimFactionsUpdater.exe](https://github.com/even86868-coder/rim-factions/raw/main/release/RimFactionsUpdater.exe) and run it:
**Install / Update** puts the newest build from this repository into RimWorld's Mods folder, **Start RimWorld** starts the game.
Everyone playing together should update before a session so all games run the same build.
