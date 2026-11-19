# FFXIV Talk Typewriter

A Dalamud plugin for FINAL FANTASY XIV that paces NPC dialogue text, typing each
line out at a configurable speed instead of showing it all at once.

The plugin writes the visible prefix of the current line into the game's own text
node just before that node is drawn. The font, wrapping, text edge and dialogue
box art remain the game's, so nothing is drawn on top of the box and box
restyling mods keep working. Writes happen in the addon's `PreDraw` event, so the
full line is never shown for a frame and then replaced.

## Features

- Global typing speed in characters per second, with per-NPC overrides by speaker
  name.
- Optional complete-line key and gamepad button, so a line can be finished
  without advancing it. Both are None by default; completion is opt-in.
- Optional hold requirement when a gamepad button is bound (default 350 ms), so a
  stray touch in a cutscene cannot finish a line. One physical press completes at
  most one line.
- Keyboard/mouse and gamepad alike, because the plugin never reads or consumes
  advance input.

## Usage

- `/talktypewriter` opens the settings window.
- Settings are saved as they are changed.
- Out of the box only pacing is active: no complete-line bind, no hold, and the
  game's own Confirm advance is untouched.

## Commands

`/talktypewriter` and the short form `/ttw` take the same arguments. A change applies
to the line being revealed and to every line after it, with no reload and no need to
reopen the window.

| Command | Effect |
| --- | --- |
| `/ttw` | Opens the settings window. |
| `/ttw 90` | Sets the global speed to 90 cps (1-200). |
| `/ttw on` | Enables pacing. |
| `/ttw off` | Disables pacing; the native reveal comes back immediately. |
| `/ttw instant on` | Reveals lines instantly. |
| `/ttw instant off` | Back to the configured speed. |

Per-NPC overrides still win over the global speed, so a speaker you gave its own
speed keeps it.

## Known limitations

- A line that repeats the previous line word for word is paced again only when
  the game announces it through the addon's refresh event. Otherwise it is drawn
  in full, which is the native behaviour.
- Cutscene dialogue is only paced where the game uses the standard Talk addon;
  coverage there is incomplete.
- Binding Confirm (South / A) as the complete-line button is supported, but the
  game uses that same press to advance, so one press both finishes the current
  line and moves on. A button the game does not use is more predictable.

## Building

Requires the .NET SDK and a local Dalamud development install; see
https://dalamud.dev for the developer setup.

```
dotnet build TalkTypewriter/TalkTypewriter.csproj -c Release
```

The plugin DLL and its manifest are written to
`TalkTypewriter/bin/Release/TalkTypewriter/`. Add the DLL path under Dalamud's
dev plugin locations to load it.

## AI disclosure

This plugin's code was written with substantial AI assistance: an AI agent
produced drafts and the author directed the design, ran the in-game experiments
and validated the result. The measurement notes behind the implementation are in
`PLAN.md`.

## License

See `LICENSE.md`.
