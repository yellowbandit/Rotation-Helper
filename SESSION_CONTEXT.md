# RotationHelper Plugin - Session Context

## CRITICAL - Reference Files (MUST READ every session)
1. `H:\OPEN CODE WOW CHAT\Reference\plugin_developer_support-page-1.txt` (4406-line Discord support channel conversation)
2. `http://localhost:5138/swagger/index.html` (DDO Dat Reader API - local web service for querying game .dat files)
3. Game client files: `D:\Games\Dungeons and Dragons Online`
4. SDK docs: `http://sdk.dungeonhelper.com/`

## Project Files
- `RotationHelperPlugin.cs` (main plugin logic - buff targeting, timers, autocast)
- `SetupSkillsForm.cs` (UI - buff cards, rotation grid, emergency grid)
- `BuffManager.cs` (reference implementation for active effect checking patterns)
- `GameDataCache.cs` (ActionDid→EffectDid cache)

## Key SDK Facts (from Discord)
1. **GetActiveEffects() is PLAYER-ONLY** - returns effects on the local player's character ONLY. Does NOT return effects on party members. Confirmed by Morrikan and RabidSquirrel multiple times.
2. **No per-entity active effects** - SDK does not expose per-entity active effects. Someone requested `GetActiveEffects()` on `IPartyMembers` but it doesn't exist.
3. **EffectDid** (0x70xxxxxx) on each IActiveEffect - can look up via `provider.GetWeenieProperties(EffectDid)` to get property collection with `Effect_Duration`, `Effect_Name`, `Effect_Icon`, etc.
4. **IActiveEffect.Properties** - dictionary keyed by DdoProperty enum values (cast to uint). Contains Effect_Duration (float, seconds), Effect_Did (0x70xx), Effect_Icon, etc.
5. **GetActiveEffects is "lossy when DH restarts"** - buffs applied before DH loaded won't appear
6. **MMF workaround** - for multi-client setups, people write effects to shared memory (MMF) and read from other processes (hyperniche approach)
7. **Bardic Inspiration reference DID**: 0x70005354 (1879049556u) - from Hypermusic's code

## DDO Dat Reader API (localhost:5138)
- OpenAPI at `http://localhost:5138/swagger/v1/swagger.json`
- Cache built from `D:\Games\Dungeons and Dragons Online` game files
- Key endpoints:
  - `POST /DbProperties/Search` - search by name words (body: `{ words: ["Greater","Heroism"], maxResults: 20, matchScore: 0.7 }`)
  - `GET /DbProperties/{id}` - get property collection by DID (0x70xxxx converted to 0x79xxxx)
  - `GET /DbProperties/IdsForName?name=X` - look up IDs by exact name match
  - `GET /DbProperties/ByWeenieType/{weenieType}` - list all objects of a weenie type (e.g., 0x000A0081 = "Spell" type)
  - `GET /DbProperties/WeenieTypeCounts` - show all weenie types with counts
  - `GET /Strings/{table}/{key}` - look up string table entries
- Finding buff durations via API is unreliable - search by name returns many false positives (class profiles, crafting ingredients, etc.)
- Better approach: read `Effect_Duration` from `IActiveEffect.Duration` at runtime when the player casts on self

## Current Architecture Issues
### Party Member Buff Detection (UNSOLVED)
- We CANNOT detect if a party member has a buff (no per-entity active effects API)
- Workaround: `_lastPartyBuffTime[key]` tracking with `PartyBuffKey(refDid, memberId, abilityName)`
- BUT: if another player casts the same buff on a party member, our timer is wrong
- Current approach: `GetActiveEffects()` pre-check for "A Party Member" type - if effect exists globally (on player), assume ALL alive party members have it (this is a heuristic)

### Spam Loop History
1. Original: fallback loop selected any alive member ignoring timers → 5-sec recast
2. Fixed: removed fallback loop → now only targets when timer expired
3. BUT: detection of existing buff on party member still doesn't work → may recast on timer expiry if we never learned the duration or if someone else refreshed it

### Buff Duration
- Hardcoded `BuffDurationTable` with ~40 entries
- `TryLearnBuffDuration()` reads `IActiveEffect.Duration` when player casts on self (only works for self-buffs)
- `DetectCooldownFromDescription()` scans property descriptions for "N minutes" / "N seconds" text
- Default fallback: 300s (5 min) if not in table and can't learn
- GH = 1200s (20 min), Death Ward = 1200s (20 min)

## Recent Changes Made
1. Fallback loop in party-member targeting REMOVED (was the source of 5-sec recast spam)
2. `_lastPartyBuffTime` keyed by string `$"{refDid}|{memberId}|{abilityName}"` instead of `(uint, ulong)` tuple
3. `GetActiveEffects()` pre-check added for "A Party Member" type in target selection
4. `GetActiveEffects()` removed from pending handler for party members (not needed, already checked)
5. Remove button "✕" added to rotation grid, emergency grid, and buff cards (bottom-right)
6. Buff card height increased to 220px for remove button space
7. Buff duration table expanded from 7 to ~40 entries
8. Default fallback increased from 120s to 300s

## DDO Dat Reader API - Experiments
- The API works and has game data from DDO client version 7900.0054.0508.4005
- Search by name works but returns many false positives for common buff names due to string table matching
- The "Spell" WeenieType (0x000A0081) only contains 186 area-effect spells (clouds, walls, storms), NOT buff spells
- Buff spells/effects are stored as 0x70xxxxxx EffectDIDs, NOT as Weenie Type objects
- To find buff duration at runtime: check `IActiveEffect.Duration` (float seconds) or `IActiveEffect.CalculateExpiration()` on the local player's active effects
- The API can be used to explore specific EffectDIDs once found via the SDK at runtime
