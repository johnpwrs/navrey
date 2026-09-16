---
name: uo-loot
description: Loot a corpse after a kill with the UO client, including containers found inside the corpse (a crate, a bag, etc.). Use whenever the user asks to loot, grab drops, or take what a killed mobile dropped.
---

# Looting a corpse

Requires a logged-in character (see `uo-login`). A corpse is just another container —
everything in `uo-containers` applies — but two things about corpses specifically are
easy to get wrong, so this skill exists to call them out.

## 0. Only loot when it's safe to stand still

Looting takes several round trips (open, list, get each item, verify empty) with the
character stationary the whole time — exactly the wrong moment to be adjacent to
something that can still hit you.

- **Nothing else hostile nearby, or the area's already clear** — loot the corpse right
  away, before moving on to the next target. Don't leave loot behind "to come back for
  later" when there's no reason not to grab it now; a corpse can decay or get looted by
  something else, and backtracking after a fight that drifted away from the corpse costs
  more than looting on the spot would have.
- **Still in danger** — another hostile adjacent, closing in, or still in combat with
  something else — finish or disengage from that fight first. Check `uo.mobiles(within=10,
  hostile=True)` (or `jq '.mobiles[]' /tmp/cuoworld.json`) before committing to a loot
  pass. Move on to the next target / retreat per `uo-combat-loop` step 5, and only double
  back for the loot once the area is clear.

## 1. The corpse must be opened before its contents are known

Same rule as any container: the server sends contents only after the corpse is
double-clicked. There is no `loot` command — use `use` to open, then `container` to list:

```bash
echo "use <corpse_serial>" >> /tmp/cuocmd
sleep 1.5
echo "container <corpse_serial>" >> /tmp/cuocmd
sleep 1
tail -10 /tmp/cuolog
```

If `container` is sent in the same batch as `use` without a pause, it can race the
server's response and come back empty even though the corpse has loot — send `use`,
wait, *then* list, rather than firing both at once.

Find the corpse's serial from `items` (it shows as `[fixed] <serial> a corpse of <creature>`
or similar, at `d=1` if you're standing next to it) — corpses aren't in the world file's
`items` list since that's filtered to movable ground items, and a kill doesn't hand you
the corpse's serial automatically.

## 2. Don't try to loot the container itself — open it in place and grab its contents

A corpse's `container` listing can include another container (a crate, a bag, a pouch)
sitting alongside ordinary loot. **`get <serial>` on that container does not work — it
stays on the corpse.** Don't bother sending `get` for it at all; go straight to opening
it where it sits:

```bash
echo "use <crate_serial>" >> /tmp/cuocmd        # open the crate itself, still on the corpse
sleep 1.5
echo "container <crate_serial>" >> /tmp/cuocmd  # now list what's inside
sleep 1
tail -10 /tmp/cuolog
# then get each item the crate listing shows — these DO move, straight to the backpack
```

This nests as deep as the loot does — a bag inside a crate inside a corpse needs two
separate open+list+get passes for the containers (crate, then bag), not one — but the
containers themselves never move, only their contents do.

## 3. Grab items one at a time, with a short pause between

Back-to-back `get` commands can trip the server's action throttle
(`System: You must wait to perform another action.`), and a throttled `get` does not
retry itself — the item stays put even though the command was accepted. A `get` that
says "Grabbed" is confirmation the packet was sent, not that the item arrived (same
caveat as `drop` in `uo-containers`).

```bash
echo "get <item1>" >> /tmp/cuocmd
sleep 1.5
echo "get <item2>" >> /tmp/cuocmd
sleep 1.5
```

## 4. Verify the source is actually empty, not just that gets were sent

After grabbing everything a listing showed, re-list the corpse (and any container
pulled from it) and confirm it comes back empty before moving on:

```bash
echo "container <corpse_serial>" >> /tmp/cuocmd
sleep 1
tail -6 /tmp/cuolog     # expect "(empty, or not opened yet - try 'use <serial>')"
```

Anything still listed did not make it into the backpack — re-send `get` for it rather
than assuming the earlier attempt worked.

## Typical full workflow

```bash
printf 'use <corpse>\n' >> /tmp/cuocmd; sleep 1.5
printf 'container <corpse>\n' >> /tmp/cuocmd; sleep 1
tail -15 /tmp/cuolog                                    # see what's on the corpse

# grab plain items one at a time
echo "get <item_serial>" >> /tmp/cuocmd; sleep 1.5

# for any container found on the corpse (crate/bag/pouch) - it stays on the corpse,
# don't try to "get" it - open it in place and drain its contents instead
echo "use <container_serial>" >> /tmp/cuocmd; sleep 1.5   # open it, still on the corpse
echo "container <container_serial>" >> /tmp/cuocmd; sleep 1
tail -15 /tmp/cuolog                                       # see what's inside it
echo "get <inner_item_serial>" >> /tmp/cuocmd; sleep 1.5

# confirm both are empty
echo "container <corpse>" >> /tmp/cuocmd; sleep 1
echo "container <container_serial>" >> /tmp/cuocmd; sleep 1
tail -10 /tmp/cuolog
```
