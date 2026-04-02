# Sentinel — Feature Ideas & Roadmap

## v1.0 — Core (Claude Code prompt ready)
- [x] Ground-projected AoE visualization (filled shapes)
- [x] All CastType shapes (circle, cone, rect, donut, cross)
- [x] Orange→red cast progress coloring
- [x] Player safety detection (am I in danger?)
- [x] Directional arrow to nearest safe position
- [x] 1,101 BossModReborn overrides
- [x] Lumina-based action lookup (no static DB, always current)
- [x] Config UI

## v1.1 — Polish
- [ ] Native omen suppression (GoodOmen-style hook, opt-in toggle)
- [ ] Castbar AoE safety indicator (see below)
- [ ] Sound warnings (configurable)
- [ ] Per-action enable/disable list

## v1.2 — Intelligence
- [ ] Multi-AoE safe zone calculation (find a spot safe from ALL active AoEs)
- [ ] Predictive AoE display (show where the NEXT mechanic will be based on cast sequence patterns)
- [ ] PvP mode: show enemy player AoE ranges and cooldown states

---

## Feature: Castbar AoE Safety Indicator

### Concept
When you're casting and standing inside (or near) an incoming AoE, show a
marker on your cast bar indicating whether you can safely finish the cast
and still escape via slidecasting, or whether you need to cancel and move
immediately.

### The Question It Answers
"Can I finish this cast and still dodge in time?"

### Data Needed (all available from Dalamud APIs)
- Player cast: remaining time, total time, slidecast window (~0.5s before end)
- Enemy AoE: remaining cast time (= time until damage snapshot)
- Player position relative to AoE edge: distance to escape
- Player movement speed: base ~6 yalms/sec, sprint ~9.6 y/s

### Math
```
slidecastStart = playerCastRemaining - slidecastWindow  (when you CAN start moving)
escapeDistance = distance from player to nearest AoE edge
escapeTime    = escapeDistance / movementSpeed
arrivalTime   = slidecastStart + escapeTime              (when you'd reach safety)
snapshotTime  = enemyCastRemaining                       (when AoE deals damage)

safetyMargin  = snapshotTime - arrivalTime
```

### Visual
- **Green zone on cast bar** — you can finish the cast safely. The marker
  shows the "point of no return" (latest you can start moving). If your
  cast progress hasn't reached it yet, keep casting.
- **Red zone on cast bar** — you need to move NOW or you'll get hit. The
  AoE will snapshot before you can escape even with slidecasting.
- **No marker** — you're not in any AoE danger zone, cast freely.

### Edge Cases
- Multiple overlapping AoEs: use the EARLIEST snapshot time and the
  LONGEST escape distance (most conservative estimate)
- Sprint active: use higher movement speed (~9.6 y/s)
- Swiftcast/Triplecast: no cast bar, no marker needed
- Instant casts: skip entirely
- AoE snapshot vs cast end: XIV snapshots damage slightly before the
  enemy cast bar finishes (~0.5-0.7s early for most abilities). This
  needs testing to find the exact offset, or we can use a configurable
  "snapshot offset" setting.

### Implementation Notes
- Read player cast state from local BattleChara.GetCastInfo()
- Read slidecast window: either hardcode 0.5s or read from game data
  (Dalamud may expose this, or NoClippy's value if available via IPC)
- The marker is an ImGui overlay drawn on top of the game's cast bar UI
  element (find the cast bar addon position via Dalamud's addon system)
- Could also be a standalone small bar near the player's character

### Why This Is Unique
No existing plugin does this. The slidecast indicator plugins (like the
one that marks when you can move) only look at YOUR cast. They don't
cross-reference incoming AoE timing. This feature combines offensive
(casting) and defensive (dodging) information into a single at-a-glance
indicator that tells you the optimal decision: keep casting or move.

---

## Feature: Raidwide Death Prediction

### Concept
For avoidable AoEs the answer is always "dodge" — showing a danger level
on them is visual noise. But for UNAVOIDABLE raidwides (CastType 5/6),
Sentinel can provide critical intelligence: will anyone in the party die?

### How It Works
1. Detect a raidwide cast (CastType 5 or 6, or known raidwide action IDs)
2. Read each party member's current HP via PartyList / PartyManager
3. Estimate whether the hit will kill them (potency vs HP heuristic)
4. Draw a warning marker (skull/red arrow) over the head of anyone at
   risk of dying, projected in world space via WorldToScreen
5. Color the overall raidwide overlay: green (safe), yellow (tight),
   red (someone will die)

### Who This Helps
Primarily healers. "Top off the Dragoon NOW" is actionable. "This circle
on the ground is dangerous" is not — the player already knows that.

### Damage Estimation
Full damage calc is too complex (potency × attack power × defense ×
buffs × variance). But a rough heuristic works:
- Track the LAST raidwide that hit the party (observe actual damage dealt
  via combat log events or ActionEffect packets)
- Use that as the baseline for the NEXT raidwide from the same boss
- If a party member's current HP < last observed raidwide damage, flag them
- This learns per-fight without needing a potency database

Alternative simpler approach: just flag anyone below X% HP (configurable,
default 50%) when a raidwide is casting. Not as smart but still useful.

### Snapshot Timing (Corrected Understanding)
Per AkhMorning research: FFXIV snapshots AoE positions when the AoE
indicator disappears (usually at cast completion). The perceived "early
snapshot" is actually network latency — the server checks your position
based on its last update from your client, which lags by your ping.

There is NO per-action snapshot offset database because that's not how
it works. The correct approach is a configurable ping-based safety
margin (default 100ms) that shifts the "danger deadline" earlier by
the player's ping. This is what we implement in v1.0.

### Implementation Priority
v1.0: action name + status effect display on all AoEs
v1.0: raidwide detection with HP-threshold party member warnings
v1.1: learned damage estimation (observe actual raidwide hits)

---

## Feature: Delayed Mechanic Tracking (Generic)

### Concept
Many boss mechanics show an AoE telegraph, then the telegraph disappears,
and the AoE resolves seconds later at the same position. The game expects
you to memorize where it was. Sentinel can keep drawing the shape until
it actually resolves — no per-fight modules needed.

This exists in dungeons (not just Savage/Ultimate). Examples: ground circles
that flash and disappear, positional markers that resolve after a delay,
exalted-style mechanics where multiple AoEs appear sequentially then all
resolve at once.

### Data Sources Available via Dalamud

1. **OnCastStarted / OnCastFinished / OnEventCast**
   - CastStarted: the cast bar appears. Sentinel already tracks this.
   - CastFinished: the cast bar ends. Currently Sentinel stops drawing.
   - EventCast: the action actually RESOLVES (deals damage). This fires
     LATER for delayed mechanics. The time gap between CastFinished and
     EventCast IS the delay.
   - Available via: ActionEffect network events, BMR-style event system,
     or by hooking the ActionEffect packet processing.

2. **Invisible Actor Spawns (ObjectTable)**
   - Many delayed mechanics spawn invisible, untargetable actors at the
     positions where AoEs will resolve. These actors are in ObjectTable
     with IsTargetable=false.
   - The actor's OID (object ID type) often identifies what mechanic it
     represents. Its position IS the AoE center.
   - Available via: ObjectTable iteration (we already do this), checking
     IsTargetable flag and filtering for combat-spawned entities.

3. **Status Effects on Bosses / Actors**
   - "Spell-in-waiting" style effects apply a status to the boss or to
     invisible actors. The status duration IS the countdown timer.
   - ActorStatus struct has: ID, RemainingTime, SourceID.
   - Available via: StatusList on any Actor (FFXIVClientStructs).

4. **Tethers Between Entities**
   - Some delayed mechanics create tether lines between actors. The
     tether ID encodes what type of mechanic it is (circle, donut,
     spread, stack). The tether endpoint positions show WHERE.
   - Available via: Actor.Tether (FFXIVClientStructs).

5. **Head Markers (EventIcon)**
   - Icon IDs appearing on players/enemies telegraph upcoming mechanics.
     Known icon IDs can be mapped to AoE shapes.
   - Available via: hooking the EventIcon handler.

6. **MapEffect / EventDirectorUpdate**
   - Arena-level changes (platforms, environmental hazards).
   - Available via: network event hooks.

### Implementation: Three Automatic Layers + Override

**Layer 1 — Cast-to-Resolution Correlation (fully automatic)**
- When CastFinished fires for action X at position P, DON'T stop drawing.
  Instead, start a post-cast timer.
- Listen for EventCast with the same action X (or a linked follow-up ID).
- When EventCast fires, record: "action X resolves T seconds after cast."
- Store this in a runtime learning dictionary: Dict<uint, float> of
  actionId → delay seconds.
- On subsequent casts of action X, Sentinel keeps the shape visible for
  T seconds after the cast bar ends.
- First pull in any content: shapes disappear at cast end (no data yet).
  Second pull onward: shapes persist through the delay.
- Persist the learned delays to a JSON file so knowledge survives between
  sessions.

**Layer 2 — Invisible Actor AoE Markers (fully automatic)**
- Each frame, scan ObjectTable for entities that are:
  - BattleNpc or EventObj
  - Not targetable (IsTargetable == false)
  - Spawned during combat (not pre-existing furniture/props)
  - Within MaxRange of the player
- Track these actors' positions. When one fires an EventCast or is
  associated with a resolving mechanic, draw the AoE shape at its
  position from spawn time until resolution.
- The shape comes from the action ID on the EventCast — same Lumina
  lookup as regular casts.

**Layer 3 — Status Effect Countdown (semi-automatic)**
- Maintain a set of known "pending AoE" status IDs (community-sourced,
  shipped with the plugin, similar to shape overrides).
- When any actor gains one of these statuses, start a countdown using
  the status duration. Draw a shape at the actor's position until
  the status expires.
- Status IDs can be community-contributed and updated without code changes.

**Layer 4 — Manual Overrides**
- For mechanics that don't fit the generic patterns, add override entries:
  `{ actionId: 12345, delaySeconds: 8.0, shape: Circle, radius: 10 }`
- Same philosophy as shape overrides — generic system handles 90%,
  overrides fix the remaining 10%.

### What This Looks Like In Practice

1. Boss casts "Delayed Eruption" — cast bar appears, Sentinel draws circle
2. Cast bar finishes — native telegraph disappears
3. Sentinel KEEPS drawing the circle with a countdown timer overlay
4. 6 seconds later, the AoE resolves — Sentinel removes the shape
5. Next time "Delayed Eruption" casts, Sentinel already knows the 6s delay

### Technical Requirements
- Hook into ActionEffect processing to detect EventCast resolution events
- Track invisible actors separately from hostile BattleNpc casts
- Persistent JSON storage for learned delay timings
- Visual distinction: delayed AoEs could use a dashed or pulsing outline
  to differentiate from active-cast AoEs
- Countdown text overlay showing seconds remaining until resolution

### Priority
This is a significant differentiator — no other generic plugin does this.
BMR requires per-fight modules. Splatoon requires manual presets. Sentinel
would learn automatically from observation.

v2.0 feature — implement after core rendering and omen suppression are solid.

---

## Feature: PvP AoE Intelligence

### Concept
In PvP (Crystalline Conflict, Frontline), show enemy player AoE ability
ranges when they're casting. PvP abilities have different ranges/shapes
than PvE versions. Lumina's Action sheet contains PvP action data too.

### What It Shows
- Enemy player casting a ground AoE → show the shape on the ground
- Enemy player ability ranges (even when not casting) → optional range
  circles showing "this player can hit you from here"
- Limit Break charge indicators on enemy players

### Why It Matters
PvP telegraphs are often faster and less visible than PvE. Showing the
actual hitbox gives you reaction time advantage. Particularly useful for:
- Dragoon high jump/limit break landing zones
- Black Mage AoE zones
- Healer AoE heal ranges (know when to spread)
- Melee cleave ranges (know when to back off)

---

## Feature: Multi-AoE Safe Zone Visualization

### Concept
When multiple AoEs are active simultaneously, compute and highlight the
safe zone(s) — areas that are outside ALL active AoEs. Draw the safe
zones in green/blue instead of the danger zones in orange/red.

### Math
This is computationally harder — it's a boolean geometry problem
(intersection of complements of multiple shapes). Approaches:
1. **Grid sampling:** Discretize the arena floor into a grid, test each
   cell against all active shapes, color safe cells green. Simple but
   potentially expensive for fine grids.
2. **Player-centric sampling:** Only compute safety in a radius around
   the player. Much cheaper.
3. **Exact geometry:** Compute the actual intersection boundaries. Hard
   for arbitrary shape combinations.

Start with approach 2 — sample a circle around the player and draw a
green "safe zone" indicator showing the nearest safe direction.
