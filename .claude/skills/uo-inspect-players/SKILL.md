---
name: uo-inspect-players
description: Inspect another character with the UO client - open their paperdoll, read everything they are wearing (with hues), and get their fame/karma title. Also tells a player apart from an NPC. Use whenever the user asks what someone is wearing, what someone's title is, who a nearby character is, or to look at/inspect/check out another player.
---

# Inspecting another character

Requires a logged-in character (see `uo-login`). Everything here reads mobiles that are **in
view** - the world file carries ~18 tiles, and a mobile that walks out of it is gone rather than
stale.

Three different facts live in three different places, and they arrive by three different routes.
Getting them confused is the whole reason this file exists:

| What you want | Command | Arrives via |
|---|---|---|
| Name + NPC job / guild tag | `click <serial>` | single-click reply, prints as `[LABEL]` |
| Fame/karma title ("The Scoundrel") | `use <serial>` **then** `gear <serial>` | 0x88 paperdoll packet |
| Everything worn, with hues | `gear <serial>` | the mobile's own equipment packets, free on sight |

## 1. Find who is nearby

Read the world file - no command, no round trip:

```bash
jq -c '.mobiles[] | select(.species | test("Human")) | {name, d:.distance, serial, x, y}' \
   /tmp/cuoworld.json
```

`species` is `Human (Male)` / `Human (Female)` for players **and** townsfolk alike, so this list is
candidates, not players. Step 3 tells them apart.

## 2. `gear <serial|self>` - what they are wearing

```bash
echo "gear 0x004FBE41" >> /tmp/cuocmd; sleep 1; tail -16 /tmp/cuolog
```

```
[GEAR] 0x004FBE41 <Name> "The Scoundrel <Name>" d=11 Innocent:
  [Backpack    ] 0x4E22CEC7 backpack
  [Tunic       ] 0x40034B7A jester suit (hue 0x0AC1)
  [Necklace    ] 0x4041E9FD necklace (hue 0x09A2 "Shadow Gold")
  [Helmet      ] 0x45DFD471 mask (hue 0x08A4 "Slimes #11")
  [Mount       ] 0x477CC194 tiller (hue 0x4001)
```

- **`gear self`** (or bare `gear`) is your own loadout - the same data as `inv`'s Equipped section,
  in the same line shape.
- **Hues come free**, named where the client's hue table has an entry (`"Shadow Gold"`) and as a
  raw value where it does not (`hue 0x0AC1`). The number is always exact; the name is best-effort.
  Matching hues across slots are how you spot a deliberate outfit - a necklace and earrings both
  in `0x09A2`, say.
- **No "open it first" step.** Unlike a container (`uo-containers`), equipment arrives with the
  mobile's own packets when it comes into view, so an empty list means they are wearing nothing -
  never that it is unknown.
- **Their backpack shows as an item; its contents never do.** The server does not send another
  character's container contents, and no command can make it. Do not report a player's backpack as
  empty - report that it is not visible.
- `[Mount]` is worth reading: it is the animal when they are riding, and a `ship`/`tiller` when
  they are standing on a boat.

## 3. Titles, and telling a player from an NPC

**The paperdoll title is not in the single-click label.** They are two different strings on the
wire. `click` gives name plus NPC job or guild tag; only the paperdoll's 0x88 packet carries the
fame/karma title, and the server sends that packet **only in reply to a double-click**:

```bash
echo "use 0x0037C70E"  >> /tmp/cuocmd; sleep 2    # double-click = open their paperdoll
echo "gear 0x0037C70E" >> /tmp/cuocmd; sleep 1; tail -3 /tmp/cuolog
```

Before the `use`, `gear` says so rather than showing a blank:

```
[GEAR] 0x0037C70E <Name> title=? (send 'use <serial>' first) d=7 Innocent:
[GEAR] 0x0037C70E <Name> "The Scoundrel <Name>" d=7 Innocent:        <- after
```

### The word order is the discriminator

Measured live at a bank in <city>:

```
"<Name> the minter"                        NPC    - job title AFTER the name
"<Name> the banker"                        NPC
"<Name> the guard"                         NPC
"The Scoundrel <Name>"                     player - karma title BEFORE the name
"The Unsavory <Name>"                      player
"The Evil <Name>, Grandmaster Merchant"    player - karma prefix + skill title
"<Name>"                                   player - neutral karma, no title at all
```

So: **a title that ends with the name is an NPC; one that starts with the name, or is just the
name, is a player.** Serial ranges are *not* a reliable discriminator - measured, an NPC sat at
`0x0039xxxx` and a player at `0x0003xxxx`, so the low/high split people expect cannot be relied
on; shards assign mobile serials however they like.

A bare name is ambiguous on its own (a neutral-karma player and a titleless NPC look identical),
so fall back to behaviour - NPCs stand in shops and repeat job barks, players move around and
answer speech (`uo-communication`).

### The title is cached per Mobile, and the cache dies with the mobile

`Mobile.Title` is set when the 0x88 packet arrives and lives on that object. **A mobile that walks
out of view and back gets a fresh object with an empty title**, so `gear` asks for the `use` again.
This is not a bug and not worth working around: just re-send `use` on seeing `title=?` for someone
already inspected. Walking toward one candidate routinely pushes the others out of the window and
resets them this way.

## 4. Opening the paperdoll itself

`use <serial>` *is* the paperdoll open - the gump appears in the game window. But note:

- **It is a client-side gump, not a server gump.** `gumps` answers `No server gumps open` with a
  paperdoll on screen, and `gumpresponse` has nothing to press. Nothing in the agent layer reads
  the gump's pixels; `gear` reads the same underlying data the gump draws itself from, which is
  why you should read `gear` rather than trying to scrape the window.
- The reply is what populates the title, so `use` is worth sending even when you only want the
  title and not the picture.

## Putting it together

```bash
S=0x004FBE41
echo "click $S" >> /tmp/cuocmd; sleep 1     # name + guild tag, prints as [LABEL]
echo "use   $S" >> /tmp/cuocmd; sleep 2     # paperdoll -> fetches the karma title
echo "gear  $S" >> /tmp/cuocmd; sleep 1     # title + every worn layer with hues
tail -20 /tmp/cuolog
```

Inspecting several at once is fine - the client serialises commands itself - but leave ~0.6s
between `use` calls so the paperdoll replies do not outrun the log, and re-read the world file
afterwards rather than trusting the serials you started with.

## Gotchas

- **`gear` needs the client binary that has it.** It was added to `CommandEngine.Actions.cs`; a
  client started from an older build answers `unknown command 'gear'`. Rebuild and restart the
  client, not just `navrey`.
- **`navrey help` does not list `gear` until `cli/Navrey.Cli.csproj` is rebuilt** - the CLI
  is a separate binary (see `CLAUDE.md`). Appending to `/tmp/cuocmd` works regardless.
- **`no mobile 0x... in view` means exactly that** - they moved out of the ~18-tile window, not
  that the serial is wrong. Re-read `/tmp/cuoworld.json` for their current position.
- **A command that found nothing still reports `ok`.** Read the `[GEAR]` line, not the exit code.
- **Nothing here is visible to the person being inspected.** `click`/`use` are ordinary client
  actions; they generate no speech and no notification. Talking to them is `uo-communication`.
