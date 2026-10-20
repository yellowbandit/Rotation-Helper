# Tracking Buffs in a DH Addon

This note describes a generic pattern for tracking buffs in a Dungeon Helper addon. It is written for addon logic that needs to decide whether a character already has a buff, whether the buff is missing, and whether it should be cast again.

## Core Idea

Track buffs by reading the character's active effects, then compare those active effects against a configured list of effect DIDs.

The addon should not rely only on "I pressed the hotbar button" or "I cast this recently." Casting can fail, be interrupted, hit the wrong target, be blocked by cooldown, or apply a different effect than expected. The most reliable signal is whether the game currently reports the expected effect on the target.

## Active Effects

An active effect is a live effect currently present on a character. For buff tracking, each active effect should be reduced to a small data record:

- `EffectDid`: the effect DID used for matching
- `IsActive`: whether the effect is currently active
- `IsHostile`: whether the effect is harmful
- `RemainingSeconds`: time remaining, if known
- `TotalDurationSeconds`: original duration, if known
- `StackCount`: current stack count, if known

Optional fields can be added for effects with extra state, such as ablative damage reduction, temporary resistance, charges, or counters.

## Which DID To Store

Store the effect DID, not the spell DID or hotbar action DID.

Many systems expose multiple identifiers:

- A spell/action DID identifies the thing being cast.
- An effect DID identifies the effect that appears on the character after the cast lands.

For buff tracking, the effect DID is the important one. The addon should usually prefer:

```text
EffectDid if nonzero, otherwise EffectId
```

Display DIDs in hex, such as `0x12345678`, because that is easier to compare with diagnostics and exported catalogs.

## Buff Rules

A buff rule should describe what to cast and which active effects prove that the buff is already present.

Example rule fields:

- `ActionName`: spell, item, clicky, or hotbar action name
- `Enabled`: whether this rule is active
- `TargetMode`: self, single party member, or group
- `EffectDids`: one or more effect DIDs that count as this buff being present
- `MinResourcePercent`: optional spell point, mana, charge, or resource threshold
- `MaxRange`: optional maximum range for party targets
- `Priority`: ordering when multiple buffs are missing

The `EffectDids` list is the bridge between the action and the active-effect snapshot.

## Matching Logic

For each target and each rule:

1. Ignore the rule if it is disabled.
2. Ignore the target if it is dead, invalid, too far away, or otherwise ineligible.
3. Read the target's active effects.
4. Check whether any active effect DID matches any DID in the rule's `EffectDids`.
5. If a match exists, treat the buff as present.
6. If no match exists, treat the buff as missing and consider casting.

Unknown or permanent duration should count as present. Positive remaining duration should count as present. Missing, inactive, or expired effects should not count as present.

## Party Buffs

For party buffing, each character needs a current state snapshot that other addon instances can read. The snapshot should include:

- Character identity
- Last update time
- Life state
- Position
- Active effects
- Optional current target, foreground state, or other eligibility flags

Use a staleness timeout. If a character has not updated its snapshot recently, do not use that snapshot for buff decisions. Stale data can cause repeated casting, missed casting, or group buffs waiting on a character that is no longer present.

## Group Buffs

Group buffs should usually cast once when:

- At least one eligible member is missing the buff.
- All eligible members are close enough to receive the group buff.
- The caster is allowed to cast right now.

Waiting for nearby members prevents wasteful repeated group casts while the party is spread out.

## Discovering DIDs

A practical DID discovery workflow:

1. Start with no buff active.
2. Capture the current active-effect list.
3. Cast the buff.
4. Capture the active-effect list again.
5. Find the new DID or DIDs that appeared.
6. Add those DIDs to the rule for that buff.
7. Re-test by letting the buff expire or removing it, then casting again.

Some buffs apply more than one effect. Some spells share an effect with another spell. Store every DID that should count as "this buff is present."

## Common Failure Cases

- The rule stores a spell DID instead of an effect DID.
- The buff applies a different effect DID than expected.
- A mass version and single-target version have different action DIDs but share the same effect DID.
- The active-effect reader misses effects with unknown or permanent duration.
- A stale party snapshot is treated as live.
- The addon rebuffs non-addon party members without any reliable way to inspect their active effects.
- A cast is blocked by movement, cooldown, resource threshold, target type, range, or foreground/manual-control policy.

## Debugging Checklist

When a buff keeps recasting:

1. Confirm the target's active effects contain the expected effect DID after the buff lands.
2. Confirm the rule's `EffectDids` list contains that same DID.
3. Confirm the DID comparison uses the same numeric basis and is not comparing names or indexes.
4. Confirm the target snapshot is fresh.

When a buff never casts:

1. Confirm the rule is enabled.
2. Confirm the rule has at least one effect DID.
3. Confirm the active-effect list does not already contain a matching DID.
4. Confirm the target is eligible.
5. Confirm the action is available and not blocked by local policy.

