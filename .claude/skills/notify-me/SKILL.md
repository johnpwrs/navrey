---
name: notify-me
description: Speak a short message out loud via macOS text-to-speech. Use whenever the user asks to be notified, told, or spoken to when a task finishes (or any other "let me know when..." request that implies an audible cue rather than just a chat message).
---

# Speaking a notification

The user is not always watching the chat. When they've asked to be notified when something
finishes, say it out loud instead of (or in addition to) writing it.

## The default voice is broken - always pass `-v Samantha`

```bash
say -v Samantha "I'm all finished!"
```

Do not call plain `say "..."` with no `-v` flag. On this machine the default voice silently
produces almost no audio (a real recorded test came back as a 0.005-second, 236-byte file - not
an error, just effectively silent), so it exits 0 and looks like it worked while the user hears
nothing. `-v Samantha` was confirmed to actually produce audible speech.

If `say -v Samantha "..."` alone is ever unreliable again, fall back to synthesizing to a file
first and playing that explicitly - this was the exact sequence used to first confirm working
audio on this machine:

```bash
say -v Samantha -o /tmp/notify.aiff "I'm all finished!" && afplay /tmp/notify.aiff
```

## When to use this

- The user explicitly says something like "notify me", "tell me", "let me know out loud", or
  "speak to me" when a task completes.
- Keep the message short - a sentence or less. This is a spoken alert, not a report; the written
  chat response still carries the actual details.
- Speak it once the task is genuinely done, not before.

## Other sounds

A plain chime instead of speech also works and is confirmed functional, if the user wants
something quicker than a spoken sentence:

```bash
afplay /System/Library/Sounds/Glass.aiff
```

Other options in the same directory: Basso, Blow, Bottle, Frog, Funk, Hero, Morse, Ping, Pop,
Purr, Sosumi, Submarine, Tink.
