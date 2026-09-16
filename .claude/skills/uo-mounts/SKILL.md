---
name: uo-mounts
description: Buy, mount, dismount and keep track of a rideable animal with the UO client — including the war-mode trap that turns a mount into an attack, telling your own animal from strays, and calling it to follow. Use whenever the user asks to buy/get/ride/mount/dismount a horse or other mount, mentions a stable or animal trainer, asks where their pet or horse went, or asks why mounting is not working.
---

# Mounts and pets

Requires a logged-in character (`uo-login`). Buying uses `uo-buy-items`; getting to the stable
uses `uo-navigation`.

## The one thing that goes wrong: war mode

**`use <serial>` on a mobile means "attack it" while war mode is on.** The same command either
mounts the animal or swings at it depending on a flag, and *the reply is identical either way*:

```
[CMD] use 0x0005C141
Used 0x0005C141
[CMD-END] use 0x0005C141 ok      <- this is what FAILURE looks like too
```

Measured live at a stable: `use` returned `ok`, `isMounted` stayed `false`, and the
attack attempt left a **target cursor hanging open** - visible in the game window as a targeting
flag the player can cancel. Nothing in the log said "you are in war mode" or "you cannot attack
that". The only signal anything went wrong was `isMounted` not flipping.

**Use the `mount` command, which handles all of this:**

```bash
echo "mount" >> /tmp/cuocmd              # our own animal, found via isPet
echo "mount 0x0005C141" >> /tmp/cuocmd   # or a specific one
```

It clears war mode, **waits for the server to confirm the flag actually cleared**, cancels any stale
target cursor, and only then double-clicks. There is no engine-level mount in ClassicUO - it only
ever double-clicks the mobile and lets the server decide - so this guard lives at our layer.

**Why the wait, and not just "turn war off then click":** `RequestWarMode` only *asks*; the flag is
cleared by a server reply. Requesting and clicking in the same frame means the click arrives while
the server still has us flagged, and it lands as an attack. Measured: war mode read `false`
afterwards and `isMounted` was still `false`, because the two crossed on the wire. `mount` polls up
to 20x50ms and **refuses to click at all** if war mode has not cleared - failing loudly beats
silently swinging at your own mount.

Doing it by hand needs the same three steps *with the same wait*, which is the reason not to:

```bash
echo "war" >> /tmp/cuocmd            # NB: a TOGGLE, not a setter - check .warMode first
echo "canceltarget" >> /tmp/cuocmd
# wait for .warMode to actually read false, then:
echo "use <mount_serial>" >> /tmp/cuocmd
```

**Check `.warMode` first rather than assuming.** Do not skip this because "we are not fighting" -
a combat script that handed off, or was killed mid-fight, leaves war mode set, and that is exactly
the state someone is in when they walk to a stable and buy a mount.

```bash
jq -c '{war:.warMode, mounted:.isMounted}' /tmp/cuostate.json
```

**Confirm with `.isMounted`, never with the exit code** - `ok` is what a failed mount looks like.

## Mount and dismount are different targets

| Doing | Command | Confirm |
|---|---|---|
| Mount | `mount [<serial>]` (aka `ride`) | `.isMounted` becomes `true` |
| Dismount | `dismount` (aka `unmount`) | `.isMounted` becomes `false` |

Underneath, those are double-clicks on **different targets** - which is the trap if doing it by hand:

| Doing | Raw equivalent |
|---|---|
| Mount | `use <mount_serial>` |
| Dismount | `use <charID>` - **yourself**, not the animal |

**Double-clicking the animal again does NOT dismount you.** Verified: while mounted,
`use <mount_serial>` returned `ok` and left `isMounted: true`. Dismounting is double-clicking
*yourself*, and your own serial is `charID` in the state file:

```bash
jq -r '.charID' /tmp/cuostate.json      # e.g. 0x0005ABED
echo "use 0x0005ABED" >> /tmp/cuocmd
```

Note `use self` does **not** work - the command wants a hex serial and answers
`expected a hex serial, got 'self'`.

## Telling your animal from the strays: `isPet`

`followers` in the state file is a **count**, not a list. It says a pet exists, not which creature
it is - useless at a stable, where every animal for sale is standing around looking identical to
yours. The per-mobile `isPet` flag in `/tmp/cuoworld.json` is the identity:

```bash
jq -c '[.mobiles[]|select(.isPet)|{name,serial,distance}]' /tmp/cuoworld.json
```

Verified outside a stable with several other animals and the vendor in view, where exactly one
mobile reported `isPet: true` and its serial matched the animal we had bought. It comes from the
server's rename flag, set for creatures under our control and nothing else.

**`isPet` does not work while mounted** - and the state file covers that case instead. Mounting
deletes the animal's *mobile* and replaces it with an item on the Mount layer, so there is nothing
in the world file's `mobiles` to match. Between the two files there is no gap:

| State | `.isMounted` | `.mount` | world `isPet` |
|---|---|---|---|
| Riding | `true` | `"Horse"` (the mount's type) | - (mobile does not exist) |
| Dismounted | `false` | `null` | `a horse 0x<serial>` (the animal) |

```bash
jq -c '{mounted:.isMounted, mount:.mount}' /tmp/cuostate.json
# {"mounted":true,"mount":"Horse"}
```

**`.mount` is a *type*, not an identity.** The Mount-layer item carries a fresh item serial
(`.mountItem`) unrelated to the animal's mobile serial, so riding two different animals of the
same type is indistinguishable - the server never sends which one it is. If the specific animal
matters, dismount and read `isPet`.

## Making it follow you

A dismounted pet does not automatically come along. The speech command is:

```bash
echo "say all follow me" >> /tmp/cuocmd
```

**There is no confirmation bark** - the pet just starts following, so do not wait for a reply or
treat the silence as failure. Verify by moving and watching the distance instead:

```bash
echo "goto <x> <y>" >> /tmp/cuocmd
# then, after arriving:
jq -c '[.mobiles[]|select(.isPet)|{name,distance}]' /tmp/cuoworld.json
```

Measured: after `all follow me`, the animal held `distance: 1` across an 8-tile walk, having
started at `distance: 0`.

## Buying one

Animal vendors sell livestock through the ordinary buy list, mixed in with goods - see
`uo-buy-items` for the `[live]` tag and why a mobile-serial stock line needs different name
resolution. Measured at one stable, from its **animal trainer** (prices differ per shard - read
them off `shop`; the order of magnitude is what carries over):

| | |
|---|---|
| Horse | 550gp |
| Pack Horse | 631gp |
| Pack Llama | 565gp |

```bash
echo "say <Vendor> buy" >> /tmp/cuocmd      # serials are re-minted on every reopen - re-read them
echo "shop" >> /tmp/cuocmd
echo "buy 0x<serial> 1" >> /tmp/cuocmd      # <Vendor>: The total of thy purchase is <N> gold.
```

The animal spawns beside you already tame, and `followers` goes up by 1 (`maxFollowers` in the
state file is the shard's cap). It is **not** mounted automatically - that is still the
three-command sequence above.

## Gotchas

- **A leftover target cursor is worth cancelling even when not mounting.** It gets answered by the
  next `target`-consuming action, so it can silently absorb an unrelated command later.
- **Nothing on disk records which animal is yours while you are riding it.** The serial lives only
  in whatever context you are holding. If it is lost, dismount and look for `isPet: true`.
- **Buying a mount is expensive relative to a grinding session.** Several hundred gold is roughly a
  full session's looting for a character starting out. Keep a bank reserve - spending the whole
  purse on a mount leaves nothing to re-equip with after a death.
