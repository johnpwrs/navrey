---
name: uo-buy-items
description: Buy items from an NPC vendor with the UO client — find the vendor, open their buy list with the "<Name> buy" speech command, list what's for sale, and purchase. Use whenever the user asks to buy/purchase something from a shop, vendor, or shopkeeper.
---

# Buying from an NPC vendor

Requires a logged-in character with the map loaded (see `uo-login`), and usually some
walking to reach the vendor (see `uo-navigation`).

## 1. Identify the vendor

Nearby mobiles are already in `/tmp/cuoworld.json` — read them rather than issuing a
`mobiles` command, which costs a round trip through the command-file poll and the log
and hands back English. NPCs show up with a generic "Human (Male/Female)"-style name
there just as they do in the command output; their real names only show up via a
single-click, which triggers an overhead name bark:

```bash
jq -c '.mobiles[] | select(.distance <= 8) | {serial, name, x, y, distance, notoriety}' \
   /tmp/cuoworld.json
```

Click each candidate to get its name (order of clicks matches order of bark replies):

```bash
printf 'click <serial1>\nclick <serial2>\n' >> /tmp/cuocmd
sleep 2
tail -15 /tmp/cuolog
```

Look for a name+profession like "<Name> the bowyer" — that's your vendor, and the shop
you want them to open usually matches their trade (bowyer → bows/arrows, tinker →
tools, etc.).

## 2. Get adjacent and open their buy list

**Double-clicking a vendor does *not* open their shop here — it opens their paperdoll.**
The real trigger is a speech command, and it must be their actual name, not the word
"vendor":

```
<Name> buy
```

e.g. `<Name> buy` with the vendor's real name — NOT `vendor buy`. This is documented
behavior on most shards (their NPC command pages list `(name) buy` — "Will show you any
goods the vendor has for sale"). Walk within about 1 tile first (`uo-navigation`), then:

```bash
echo "say <Name> buy" >> /tmp/cuocmd
sleep 2
tail -8 /tmp/cuolog
```

A successful open shows the vendor's greeting in the log (e.g. `<Name>: Greetings. Have
a look around.`) and, shortly after, an `[INFO] [SHOP] Vendor ... buy list ready: N
items` line.

### Why plain "say" text alone isn't enough

This was the hard-won part of getting `opendoor`-style speech commands working here.
UO has a legacy **keyword speech** system (`speech.mul`): the real client checks
outgoing speech against a table of command phrases (`bank`, `guards`, `*buy*`, etc.),
and if it matches, sends a completely different **encoded** packet — bit-packed keyword
IDs plus the text — instead of plain UTF-16 text, with the message type OR'd with
`0xC0`. Many servers' NPC command scripts (including shards this has been tested on) only
recognize the *encoded* form; plain text with the identical words is visibly broadcast and echoed
back to you, but silently ignored by the command parser. `Send_Speech` in
[OutgoingPackets.cs](../../../cli/Packets/OutgoingPackets.cs) now replicates this:
the client loads `speech.mul` at startup, matches
the outgoing text against it (see
[Map/SpeechKeywords.cs](../../../cli/Map/SpeechKeywords.cs)), and encodes
accordingly. If a future speech-triggered command mysteriously does nothing even though
the words are right, this encoding is the first thing to double check — not the
phrasing.

Also worth knowing: while chasing this bug, a second unrelated liveness-check mechanic
was found and fixed — the server periodically sends a blank "SYSTEM" speech packet as a
ping, and a real client answers with a fixed magic-byte ACK (`Send_ACKTalk`, see
`IncomingPackets.cs` `Handle_AsciiSpeech`). This wasn't confirmed as the actual cause of
the vendor issue (the keyword encoding was), but it's implemented now regardless since
some servers silently penalize a connection that never answers it.

## 3. List what's for sale

```bash
echo "shop" >> /tmp/cuocmd
sleep 1
tail -15 /tmp/cuolog
```

Prints every item currently in the vendor's buy list with its serial, name, graphic,
price, and stock amount, e.g.:
```
Vendor [000012F3] buy list (8 items):
  [400E3689] arrow fletching           Graphic:0x1022 Price:2 Amt:20
  [400E3693] arrow                     Graphic:0x0F3F Price:8 Amt:80
  ...
```
If `shop` says no buy list has been received yet, the speech command either didn't
reach the vendor (check adjacency) or hasn't been processed by the server yet (wait a
moment and retry `shop`).

## 4. Buy

```bash
echo "buy <item_serial> <amount>" >> /tmp/cuocmd
sleep 2
tail -8 /tmp/cuolog
```

e.g. `buy 400E3693 20` to buy 20 arrows. A successful purchase gets a confirmation bark
from the vendor stating the total cost. `buy` always targets whichever vendor's buy
list was most recently opened — no need to pass the vendor's serial.

Confirm the purchase:
```bash
printf 'status\ninv\n' >> /tmp/cuocmd
sleep 2
tail -12 /tmp/cuolog
```
`status` shows updated gold; `inv` shows the new item in the backpack. Note this
relies on the backpack already having been opened at least once this session (see
`uo-equip-items` step 1) — if `inv` still shows an empty/stale backpack despite the
gold having gone down, `use <backpack_serial>` first, then re-check `inv` before
concluding the purchase didn't land.

## Gotchas

- **Vendors wander.** These aren't fixed behind a counter — they walk around a small
  area. Re-read the world file for current position immediately before walking to them;
  a position from more than a few seconds ago may already be stale.
- **The buy-list serial isn't the vendor's serial.** `shop` output serials are the
  individual stock items; `buy` takes one of those, not the vendor's own mobile serial.
- **Selling is a separate skill - see `uo-sell-items`.** It works now (`<Name> sell`, then
  `selllist` / `sell <serial> <amount>`), but almost none of the buy-side mechanics above carry
  over: a sell-list serial is an item in your own backpack rather than vendor stock, and the sell
  list does *not* close after each sale - so the reopen-and-re-read loop buying needs is wrong
  there.

## Cheap bandages: buy cloth and cut it

A provisioner sells bandages outright, but a tailor sells the raw material for about half as much,
and scissors turn one into the other. Measured at a tailor on one shard (prices vary by shard -
read them off `shop`; the ratio is what carries over):

| Bought | Price | Cuts into | Effective |
|---|---|---|---|
| Cloth | 3gp each | 1 bandage each | 3gp/bandage |
| Bolt Of Cloth | 195gp | 50 cloth -> 50 bandages | 3.9gp/bandage |

A bolt needs **two** passes of the scissors, and the intermediate item is **`cut cloth`**, not
`cloth`: `bolt of cloth -> cut cloth -> clean bandage`. Loose `cloth` bought off the shelf is one
pass (`cloth -> clean bandage`). Measured 2026-08-30 at a tailor: 2 bolts cut into 100
`cut cloth`, which cut into 100 bandages; 80 loose `cloth` cut straight into 80 bandages.

### `buy` takes a whole cart - don't loop when one call will do

The 0x3B buy packet carries a **list** of items (a real client fills a cart and sends one packet),
so `buy` takes any number of `<serial> <amount>` pairs and sends them as a single request:

```bash
# one packet, one round trip, one "total of thy purchase" bark
echo "buy 0x400740FB 1 0x40074102 1 0x400740F8 1 0x400740F6 1" >> /tmp/cuocmd
```

Quantity and multiple lines compose freely - `buy <bolt> 2 <weapon> 2` buys two of each. Measured
at a tanner: `buy <cap> 2 <gloves> 1` returned `The total of thy purchase is 42
gold` for 12+12+18, and a five-line buy-back of a sold suit landed all five in one call.

**Read the whole shopping list off one `shop` before buying**, then spend it in one `buy`. The
reopen-per-item loop below is only needed when a *single* line has to be bought repeatedly (the
20-per-line cap), not for buying several different things.

### Two things that make a naive buy loop fail

- **A purchase closes the buy list.** A second `buy` without re-sending `<Name> buy` gets
  "Thou hast bought nothing!" from the vendor - which reads like an empty shelf but is not.
- **Reopening the list mints fresh serials for every stock line.** So the line's serial has to be
  re-read from `shop` on every pass; a remembered serial is stale the moment the list reopens.
  This is why the script takes the stock line's *name* and looks the serial up each time.

Both are reasons to batch: one `buy` with every line in it never reopens, so it never restales.
A single line is still capped at 20 units per call regardless of what the stock amount says.

### Livestock is sold in the same list as goods - `[live]` says which

Animal trainers and stablemasters sell **mobiles**, not items, and both arrive on the same 0x74
buy list. The two are told apart by serial range, which is a protocol invariant rather than a
guess:

| Serial range | What it is | Where its name comes from |
|---|---|---|
| `0x00000001`-`0x3FFFFFFF` | a mobile (an animal) | `graphic` is a **body** ID -> `MobNames` |
| `0x40000000`-`0x7FFFFFFF` | an item | `graphic` is a tiledata ID -> the item's own name |

`shop` tags the mobile lines `[live]`:

```
0x40326286      34gp  x20   Shepherd's Crook
0x000242A2     550gp  x9    Horse [live]
0x000242A3     631gp  x10   Pack Horse [live]
0x0002429F     132gp  x10   Cat [live]
```

**A body graphic named against the item tiledata produces confident nonsense, not an error.** Before
this was fixed, an animal trainer's live stock listed as `Marble Arch`, `Stone Arch` and `Ankh` -
plausible-looking decor at plausible-looking prices, with nothing to suggest the list was wrong.
If an animal vendor appears to sell masonry, this is what is happening.

Live animals run from about 100gp for the cheapest pets into the low thousands for the largest
mounts and beasts; read the actual prices off `shop` rather than assuming.

Buying is the ordinary `buy <serial> 1` - the animal spawns beside you already tame and follows.
A mount then wants `uo-mounts`' mounting step, not a container move.
