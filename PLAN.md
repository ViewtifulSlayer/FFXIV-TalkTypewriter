# TalkTypewriter v2 - Native Reveal Plan

Status: P1 shipped as a native prefix pacer (built clean, not yet run in game); P2-P4 awaiting approval
Scope: the `Talk` addon only (quest dialogue and cutscenes)
Supersedes: the v0.1.0.2 overlay implementation (session 20260910-01)

## 1. Decision summary

Stop hiding the native dialogue text node and redrawing the line with ImGui.
Instead, write a truncated prefix of the line into the game's own
`AtkTextNode` each time the visible character count changes. The game then
renders the text itself: its font, edge colour, kerning, wrapping, line height
and box art. There is no rendering to reproduce, so fidelity is native by
construction.

Decisions taken for this pass:

- Confirm keys are left entirely to the game. Pressing Space/Return/Numpad0
  advances the dialogue as normal, skipping the rest of the reveal. No input
  hook.
- A separate, configurable non-confirm key completes the current line in place
  without advancing.
- `Talk` only. `_BattleTalk` and `_MiniTalk` are out of scope.
- Auto-advance lines are retimed only if the probe confirms the `AtkValue`
  indices (see section 4).

## 2. Evidence

### 2.1 Verified in the installed toolchain

Dalamud 15.0.4.0, FFXIVClientStructs commit `12e318c`. The following members
are present in the installed `FFXIVClientStructs.dll`:

`GetTextNodeById`, `GetNodeById`, `SetText`, `ResizeNodeForCurrentText`,
`ApplyTextFlow`, `GetTextDrawSize`, `BuildFontCache`, `FreeFontCache`.

`AtkTextNode` layout facts (upstream `AtkTextNode.cs`):

- `NodeText` (`Utf8String`, 0xD0) stores a copy of `OriginalTextPointer`.
- `SetText(string)` and `SetText(ReadOnlySpan<byte>)` copy into `NodeText` and
  then set `OriginalTextPointer = NodeText.StringPtr`.
- `ApplyTextFlow()` applies `TextFlags.Ellipsis` or `TextFlags.WordWrap` to the
  text from `OriginalTextPointer` and stores the result in `NodeText`. This is
  the one behaviour that decides the whole design (see section 4).
- `TextFlags`: `AutoAdjustNodeSize` = 1, `Bold` = 2, `Italic` = 4, `Edge` = 8,
  `WordWrap` = 64, `MultiLine` = 128, `OverflowHidden` = 256,
  `Ellipsis` = 1024, `FontCache` = 16384.
- Geometry and type: `FontSize` (0x165), `LineSpacing` (0x162),
  `CharSpacing` (0x163), `AlignmentFontType` (0x164) where bits 0-3 are
  alignment and bits 4-7 are font type, `FontCacheHandle` (0x168),
  `TextColor` (0xC4), `EdgeColor` (0xC8), `TextId` (0xC0).

`AddonEvent` includes `PreRefresh`, `PostRefresh`, `PostUpdate`, `PreDraw`,
`PreHide`, `PreFinalize`. `AddonRefreshArgs` exposes a raw `AtkValues` pointer
plus `AtkValueCount`.

### 2.2 Prior art that does the same thing

- Echoglossian `NativeUI/AddonHandlers/Talk/TalkHandler.cs` mutates the live
  Talk text node at `PostUpdate` and `PreDraw` with
  `TextFlags = WordWrap | MultiLine | AutoAdjustNodeSize`,
  `SetWidth(parent->GetWidth())`, `SetText(...)`, then
  `ResizeNodeForCurrentText()`. It captures a presentation snapshot of
  `FontSize`, width and `TextFlags`, and restores only if it dirtied them.
- Same handler supplies the node ids: Talk body text = **3**, speaker name = 2,
  wrapper/parent = 10, accessed through `GetTextNodeById`.
- SimpleTweaks `TalkAdjustments` proves `AtkValue` mutation in `PreRefresh` is
  safe and shipping, writing indices 3 (style) and 5 (chat log channel).

### 2.3 Where we differ from that prior art

Both references replace text at a fixed length and want the box to *grow*. We
replace text with a prefix that grows character by character and want the box to
*stay put*. That is the entire design risk, and it is why the probe in section 4
comes first.

## 3. Defects in v1 this removes

| Defect | Cause in v1 | Resolved by |
|---|---|---|
| Text renders in the ImGui default font, no edge, no markup | hides `AtkTextNode`, redraws via `GetBackgroundDrawList().AddText` | native `SetText` |
| Italics and colour codes lost | `StripMarkup` returns `SeString.TextValue` | prefix builder keeps payload bytes |
| Per-NPC overrides never match | `AtkValue` indices reversed: v1 assumes name = 0, text = 1; live capture reads text = 0, name = 1 | runtime index assertion (section 6.1) |
| `AccessViolationException` risk on stale node pointers | deref guarded only by "addon exists" | `IsLive()` membership check |
| Per-frame log spam and per-read `Skip(i).First()` allocation | `LogAtkValues` on every refresh | probe-only, direct `AtkValues` indexing |
| Unbounded `previousTexts` set | changed-text heuristic | heuristic deleted |

## 4. Open questions, answered by the P0 probe

The probe is the first milestone of the implementation, not a throwaway build.
It ships behind `DebugProbe` in the real plugin and runs at a forced 2 cps.

| Question | Measurement | Consequence |
|---|---|---|
| Is wrapping baked into `NodeText` or applied live at draw? | does `NodeText` contain `\n` at `PostRefresh`; does `ApplyTextFlow` add them | if baked, every `SetText` must be followed by `ApplyTextFlow`; if live, calling it would double-wrap |
| Does the box reflow when text shortens? | `Height` before/after a shortened `SetText`, plus wrapper node 10 height | if it reflows, pin `Height` (and possibly the wrapper) from the snapshot |
| Is `TextFlags.AutoAdjustNodeSize` set on the live node? | flag dump | must be cleared while trimmed, restored at line end |
| Is `TextFlags.FontCache` set? | flag dump | if set, each change needs `FreeFontCache` / `ApplyTextFlow` / `BuildFontCache`; measure cost |
| Does anything overwrite our write mid-line? | `NodeText.Length` one second after writing | if yes, re-apply in `PreDraw` instead of `PostUpdate` |
| Are `AtkValue[6]` (auto-advance) and `[7]` (delay ms) correct? | dump `AtkValue[0..7]` types and values on an auto-advancing line | if unconfirmed, skip retiming entirely (feature disabled) |
| Which `AtkValue` index holds the line text? | compare `[0]` and `[1]` against the resolved node text | settle text vs name index at runtime |

Probe procedure, on a single line:

1. Log every text node: `NodeId`, `TextId`, `TextFlags` hex, `Width`, `Height`,
   `Y`, `FontSize`, `AlignmentFontType`, `NodeText` length and first 32 chars.
2. Log `AtkValue[0..7]`: type plus a short preview.
3. Experiment A: `SetText(prefix at 40%)` without `ApplyTextFlow`. Dump metrics,
   screenshot.
4. Experiment B: same prefix with `ApplyTextFlow`. Dump metrics, screenshot.
5. Experiment C: restore full text and original flags. Dump metrics.
6. One second after each write, re-read `NodeText.Length` to detect overwrites.

## 5. Phases

### P0 - Instrumented probe

- New `Talk/DebugProbe.cs` (~90 LoC), guarded by `config.DebugProbe`.
- Forces `SpeedCps = 2` while enabled.
- Done when all seven questions in section 4 have a recorded answer in
  `PLAN.md` (appended as "Probe results") and two screenshots exist.

### P1 - Prefix builder (pure, no game required)

- New `Talk/SeStringPrefix.cs`:
  `public static byte[] Truncate(SeString line, int visibleChars, bool wordSafe)`.
- Walks `line.Payloads` in order. Text payloads are copied up to the budget
  (`new TextPayload(t.Text[..take]).Encode()`); every other payload is emitted
  with `payload.Encode()` so control bytes stay in the original order. When the
  budget is exhausted, the builder re-emits the source's own close payloads for
  any styling opened before the cut, so colour or italics cannot leak past the
  prefix. Unstyled lines (the overwhelming majority) reduce to concatenating
  text encodings.
- `wordSafe` backs the budget up to the last whitespace in the final text
  payload.
- Done when: a round trip of `Truncate(full, fullLength)` byte-equals the
  original encoding on a styled and an unstyled sample, and the P0 hex dump of
  a real line matches.

### P2 - Native node layer

- New `Talk/NativeTalkText.cs` (~140 LoC, `unsafe`). The only file that touches
  node memory.
- `Resolve(AtkUnitBase*)`: `GetTextNodeById(3)`, validated against `IsVisible`
  and presence in `NodeList`. Fallbacks: the text node whose `NodeText` matches
  the parsed line, then the widest visible text node with `TextId == 0`.
- `Snapshot()` once per line: `Height`, `Width`, `Y`, `TextFlags`, `FontSize`,
  `AlignmentFontType`, and the full text bytes.
- `ApplyPrefix(ReadOnlySpan<byte>)`, `Restore()`, `IsLive()`, `Reset()`.
- Done when: builds clean and P3 shows correct rendering with a static box.

### P3 - Pacing state machine

- Rewrite `TalkPacing.cs` (~120 LoC), keeping the class name so `Plugin` and
  `ConfigWindow` change minimally.
- `PreRefresh`: capture the line bytes from the text `AtkValue`, parse to
  `SeString`, resolve the speaker from the name `AtkValue`, compute effective
  cps, start the line.
- `PostRefresh`: the addon has applied and laid out the full text. Snapshot
  geometry, then apply prefix 0.
- `PostUpdate`: `shownChars = floor(elapsedSeconds * cps)`, clamped to the
  plain-text length; apply on change only.
- `PreDraw`: re-apply if `NodeText` byte length differs from the prefix length.
  Kills the one-frame flash at line start and repairs external overwrites.
- `PreHide`, `PreFinalize`, disable, `Dispose`: restore.
- Delete the `IFramework.Update` and `UiBuilder.Draw` subscriptions.
- Done when: one line at 35 cps renders in the game font with correct edge,
  wrapping, static box size, and the chat log still receives the full line.

### P4 - Advance coordination, config, UI

- New `Talk/TalkAdvance.cs` (~40 LoC): in `PreRefresh`, if auto-advance is
  already enabled on the line and `AdjustAutoAdvance` is set and P0 confirmed
  the indices, write `AtkValue[7] = max(existing, revealDurationMs)`. Never
  enables auto-advance where it was off. Never touches `AtkValue[4]` (input
  blocking).
- `Configuration`: `Version` to 1 plus the fields in section 7.
- `ConfigWindow`: `CompleteKey` selector, `WordSafeReveal` and
  `AdjustAutoAdvance` checkboxes, `DebugProbe` checkbox, and a status line of
  the form `{speaker} - {shown}/{total} chars @ {cps} cps`.
- `TalkTypewriter.csproj`: version `0.2.0.0`.
- Done when: an auto-advancing line is not cut short, and the completion key
  finishes a line without advancing.

## 6. Core routines

### 6.1 Line capture and index assertion

```
PreRefresh(args):
    values = (AtkValue*)args.AtkValues
    if values == null or args.AtkValueCount < 2: return
    nodeText = resolved node text, if a node is known
    textIndex = (values[0] matches nodeText) ? 0 : (values[1] matches nodeText) ? 1 : 0
    if textIndex != 0: log warning once per session (assumption changed)
    lineBytes = utf8 bytes of values[textIndex]
    speaker   = utf8 of values[1 - textIndex]
    full      = SeString.Parse(lineBytes)
```

The assertion makes the plugin self-correcting if a patch swaps the order, and
it retroactively explains the v1 override bug.

### 6.2 Applying a prefix

```
ApplyPrefix(prefix):
    if not IsLive(): return
    if node.NodeText.Length == prefix.Length: return        // byte length
    if not snapshotTaken: Snapshot()

    node.TextFlags = (snapshotFlags | WordWrap | MultiLine)
                     & ~(AutoAdjustNodeSize | Ellipsis)
    node.SetText(prefix)

    if probeSaysWrapIsBaked: node.ApplyTextFlow()
    if (snapshotFlags & FontCache) != 0:
        node.FreeFontCache(); node.ApplyTextFlow(); node.BuildFontCache()

    node.SetWidth(snapshotWidth)
    node.SetHeight(snapshotHeight)      // only if height reflowed in P0
    node.SetY(snapshotY)
```

Clearing `AutoAdjustNodeSize` is what prevents the box from collapsing as the
prefix grows. `Ellipsis` must go too, or the game appends "..." to the prefix.

### 6.3 Restoring at line end

```
Restore():
    if not snapshotTaken: return
    if not IsLive(): Reset(); return
    node.TextFlags = snapshotFlags
    node.SetText(fullTextBytes)
    node.ResizeNodeForCurrentText()
    node.SetWidth(snapshotWidth); node.SetY(snapshotY)
    Reset()
```

## 7. Configuration

| Field | Type | Default | Purpose |
|---|---|---|---|
| `Enabled` | bool | true | master switch (unchanged) |
| `SpeedCps` | float | 35 | global speed (unchanged) |
| `InstantText` | bool | false | native behaviour (unchanged) |
| `NpcSpeeds` | dict | empty | per-speaker overrides (unchanged, now actually matched) |
| `CompleteKey` | VirtualKey | `NUMPAD5` | completes the current line, does not advance |
| `WordSafeReveal` | bool | false | reveal whole words instead of partial ones |
| `AdjustAutoAdvance` | bool | true | retime auto-advance to the reveal |
| `DebugProbe` | bool | false | P0 instrumentation, forces 2 cps |
| `Version` | int | 1 | config migration |

`CompleteKey` must never be Space, Return or Numpad0: the game consumes those to
advance, so binding one would compete with the native advance. The settings UI
offers None plus a short list of non-confirm keys.

## 8. Verification and build loop

1. `dotnet build --configuration Release` from
   `projects/recent/talk-typewriter` (writes `obj/` and `bin/`).
2. Dev plugin path: `TalkTypewriter/bin/Release/TalkTypewriter.dll`, not the
   packaged `latest.zip` folder.
3. Reload: unload/reload in `/xlplugins` Dev Tools, or restart the client.
4. Logs: `/xllog` at Debug while `DebugProbe` is on. Settings:
   `/talktypewriter`.
5. Per-phase gates are listed as "done when" in section 5. Expect 2-3 launch
   cycles; node behaviour has to be observed, not inferred.

## 9. Risks and fallbacks

| Risk | Mitigation | Fallback |
|---|---|---|
| Box collapses as the prefix grows | clear `AutoAdjustNodeSize`, pin `Height` from snapshot | pin wrapper node 10 as well |
| Text renders as one clipped line (wrap not live) | `ApplyTextFlow` after each `SetText` | build line breaks ourselves using `GetTextDrawSize` per word |
| `SetText` at 35/s stutters | write only on character change; measure in P0 | hop to a 20-30 Hz step, or bypass the font-cache path |
| Partial last word jumps to the next line when completed | `WordSafeReveal` | accept it, only the invisible tail is affected |
| Game rewrites the node mid-line | `PreDraw` length check re-applies | move all applies to `PreDraw` |
| Another plugin also writes Talk text | we re-apply on mismatch each draw | document the conflict, no automatic resolution |
| Stale node pointer | `IsLive()` membership check before every deref | clear state on `PreFinalize` and `PreHide` |

## 10. Deletions

ImGui overlay draw path, `NodeFlags.Visible` hide/restore, `StripMarkup`, the
`previousTexts` changed-text heuristic, the per-refresh `LogAtkValues` dump, the
`UiBuilder.Draw` subscription and the `IFramework.Update` subscription.
Roughly 200 lines removed, 350-420 added.

## 11. Effort

| Phase | Work | Launch checks |
|---|---|---|
| P0 | ~1 hour | 1 |
| P1 | ~2 hours (pure, testable offline) | 0 |
| P2 | ~3 hours | compile only |
| P3 | ~4 hours | 1-2 |
| P4 | ~2 hours | 1 |

No new runtime dependencies. One optional test project for P1 (see section 12).

## 12. Out of scope

- `_BattleTalk`, `_MiniTalk`, `TalkSubtitle`.
- Word-by-word reveal, per-NPC colours, custom positioning, custom box art.
  Those belong to the separate custom-box project (option B2 in session
  20261118-01).
- `.github/workflows/pr-build.yml` still references `SamplePlugin` and branch
  `master`; it is broken and left alone.
- Publishing to the Dalamud feed.

## 13. Decisions log

| Date | Decision | Rationale |
|---|---|---|
| 2026-11-18 | Native text-node mutation over ImGui overlay | game renders font, edge, wrap and box art itself |
| 2026-11-18 | Run the probe as P0 of the real plugin | answers five unknown native behaviours in one build |
| 2026-11-18 | Confirm keys left to the game | avoids an input hook and a class of crashes |
| 2026-11-18 | Separate non-confirm key completes a line | keeps player agency without fighting the advance input |
| 2026-11-18 | `Talk` only in this pass | prove the mechanism on one addon first |
| 2026-11-18 | Auto-advance retiming gated on probe evidence | indices 6/7 are unverified in shipping code |

## 14. Probe results

Not run yet. Results are appended here after P0.

### How to run P0

1. Build: `dotnet build TalkTypewriter/TalkTypewriter.csproj -c Release`. The
   plugin lands at `TalkTypewriter/bin/Release/TalkTypewriter.dll`, not in the
   packaged subfolder.
2. Add that DLL to the Dalamud dev plugin locations (`/xlsettings`, Experimental).
3. Load it (`/xlplugins` -> Dev Tools -> Installed Dev Plugins), then run
   `/talktypewriter` and tick "Debug probe (P0)". Pacing is inert while it is on.
4. Trigger a dialogue line of at least 24 characters. The probe arms once, runs
   A -> B -> C about 1.5 s apart, and requests two screenshots through the game's
   own `/screenshot`. Toggle the flag or press "Re-arm probe" to run it again.
5. Collect the `[Probe]` lines from `/xllog` (Information level) and the two
   screenshots from the game's screenshot folder.

### What to read in the log

| Line | Answers |
|---|---|
| `armed on a N char line (node id 3)` | which node was used, and whether the id-3 assumption held |
| `text index check \| [0] matches=... [1] matches=...` | whether the line text is AtkValue 0 or 1 |
| `A written` then `B before ApplyTextFlow` | `overwrite=STILL-OURS` means the game does not rewrite the node mid-line |
| `A written` versus `B after ApplyTextFlow` | compare `lines=` and `draw=`; a jump in `lines` proves wrap is baked and `ApplyTextFlow` is required |
| `h=` across armed, A, B, C | whether the box reflows when the text shortens |
| `flags=...` | `autoResize`, `fontCache`, `wrap`, `multi`, `ellipsis` as found on the live node |
| `AtkValues count=...` | indices 6 and 7 on an auto-advancing line |

### Reveal watch

Second probe mode, added after the first run showed the game reveals text faster
than the probe's 1 s settle but not instantly. The client exposes
`SimpleTweenValueType.NodeText` ("cast from float to int, then converted to an
Utf8String"), which is a character counter, so the reveal may be a tween that can
be retimed instead of reimplemented.

`RevealWatchProbe` samples `NodeText` length every frame for up to 3 s after a line
appears (stops after 12 stable frames or 180 frames), logs the length timeline and
the average rate, and scans `AtkStage.Instance()->AtkSimpleTweenHolder` for tweens
whose `Node` is the text node (state, duration, easing, animation types). It writes
nothing to the node. Enable "Reveal watch (frame sampler)" in `/talktypewriter`;
it samples 3 lines then stops until re-armed. Read the `[RevealWatch]` lines in
`/xllog`.

### Results

Three lines measured 2026-11-18 (61, 200 and 221 chars). Each run armed on the
first frame the text node held the new line, and that frame already held the whole
line:

| line | chars | original | frames | changes | tween on node |
|---|---|---|---|---|---|
| 1 | 61 | 61 | 12 | 0 | no |
| 2 | 200 | 200 | 12 | 0 | no |
| 3 | 221 | 221 | 12 | 0 | no |

`timeline` recorded one sample per line (`61@0ms`, `200@0ms`, `221@0ms`), so the
node text length never changed after the new line was visible. The sampler compares
against the previous line every frame, so a slower reveal would have been caught at
its first partial string; none was. The tween holder stayed readable
(`active=26` at every arm) with no tween on the text node (`onNode=0`), so the
NodeText tween hypothesis is dead: the game does not animate the line through
`AtkSimpleTween`.

`original` equals `text` at arm on all three lines, so `OriginalTextPointer` points
at the same complete string; there is no full-length source behind a shorter
prefix.

Conclusion: the game writes each line into `NodeText` in a single frame and never
grows it. Whatever reveal the player perceives is either drawn outside the node or
is the box opening, so it cannot be retimed from node state. P1 stays "own the
prefixes", with one added requirement: the prefix must be written in the same frame
the game sets the text, otherwise the full line shows for one frame.

### P1: what shipped

`TalkPacing` no longer draws anything. The ImGui overlay, the `NodeFlags.Visible`
hide/restore, the `UiBuilder.Draw` subscription, the `ApplyTextFlow` call, the
geometry and flag writes and the `AtkValues` dump are all gone. It writes UTF-8
prefixes into the text node with `AtkTextNode.SetText` (buffer with a trailing zero
byte) and leaves `TextFlags` and geometry untouched, because the native node already
wraps at draw time.

Per line it captures the full bytes, splits them into one step per character
(multi-byte UTF-8 kept whole, SeString payloads between 0x02 and 0x03 released
atomically so markup is never cut in half), writes one character in the same frame
the game sets the line, then advances on a wall clock at the effective cps.
`CompleteKey` (default Numpad 5, configurable, never Space, Enter or Numpad 0)
writes the rest of the line without advancing. A gamepad button does the same through
`IGamepadState.Pressed` (`CompleteButton`, default R3), because a controller cannot
press a key.

Confirm/South is the game's own advance key, so completing on it also advances. Two
attempts at intercepting that press were built, and both are now removed: detouring
`AtkUnitBase.FireCallback` / `FireCallbackInt` and dropping the advance while a line
revealed. It worked for plain lines but wedged conversations that offer choices: an
option selection arrives through the same callback, and the option prompt is re-fed
through refresh while the line still counts as revealing, so the press that should
have picked an option finished the line instead and the menu never resolved. There is
no reliable way to tell an advance from a selection at that seam, so the detour and its
`FinishLineOnAdvance` option were deleted rather than shipped as a third variant. Bind
a button the game does not use for advancing (East, R3); native Confirm still skips the
rest of a line.

Separate bug, found in the same report: a new conversation reuses the Talk addon
instance, so neither setup nor finalize is guaranteed between two talks with the same
NPC. The previous line's completed state therefore survived, and an identical first
line was recognised as "already complete" and drawn in full. That is why pacing applied
only on the first talk with an NPC and only a different NPC reset it. `TalkPacing` now
drops the stale state on `PreHide`, and on `PostShow` when the previous line had
finished; a line that is still revealing is left alone so its prefix is never frozen.

Talk refreshes several times a second and the old code restarted the line on every
refresh, which alone pinned the reveal at character zero. A refresh is now the same
line when the node text equals what was written, or equals the captured full line
(re-apply the prefix without resetting the clock). The node is identified as node 3,
with the longest text node as fallback, and the speaker as node 2 for per-NPC speeds.
Timing is owned by the addon's `PreDraw` event, which fires immediately before
`AtkUnitBase.Draw`. `Framework.Update` was tried first and left a one-frame flash:
it can run after the addon has already been drawn, and the game sets a new line
during its update. PreDraw cannot be late. Each frame it re-asserts the prefix if the
node no longer holds our text (same line restored, or a different line started), then
advances the clock-based reveal. Node contents are compared as strings, so the check
cannot loop on a normalised read-back.

### Repeated identical lines: attempted and reverted

A line whose text repeats the previous finished line cannot be detected by comparing
strings: a refresh that puts our own completed text back reads identically. Two attempts
were shipped and both were removed after in-game feedback.

The first used `AtkTextNode.OriginalTextPointer` (native code stores the pointer it was
handed) plus a pinned write buffer to ask who wrote the node last, with a 200 ms input
window to separate an advance from a passive refresh. It worked for the case it was built
for, but any pad press makes the box refresh and write the full line back, so a finished
line plus any button read as a new identical line and retyped from the start. It was
worse than the bug it fixed.

The pre-draw guard is back to string comparison only: our own text is left alone, the
full current line is re-prefixed with the clock untouched, anything else starts a line.
The write path is back to a plain per-write buffer. The known cost is accepted: a line
that genuinely repeats the previous one is drawn in full instead of re-paced. Nothing in
the node text separates the two cases; the event that does is documented below.

Confirm/South stays native. The probe results above are the reason, not an unfinished
idea: the game writes a whole line in a single frame, with no tween and no partial node
state, so there is no reveal for that press to finish and Confirm is simply the advance.
Keeping the rest of the line means cancelling the advance, and that seam is shared with
option selection - both detours that tried it wedged choice prompts. East and R3 are the
complete-line bindings.

### The line-change signal

The Dalamud log from a long cutscene settled it, and produced two facts:

- the Talk box receives `InputReceived` about fourteen times a second while a line
  reveals, with no button pressed. Any rule keyed on "input arrived recently" is
  therefore always true, which is why the removed heuristic fired on every button and
  would have fired on nothing at all;
- `RefreshAddon` is only called when the caller sets the line. Of the refreshes in that
  session, every one carried a new line and none landed on a finished line, so a repaint
  never reaches the addon's refresh event.

That makes `PostRefresh` the line-change signal: text identical to the finished line
arriving there is a new line that repeats the previous one, and it is re-paced. The
pre-draw guard still never restarts identical text, so a repaint cannot retype a line.

A second, larger cause of the repeated text was the pacer's own state being cleared when
Talk hid or showed: the next pre-draw then found itself inactive and started the line
again from whatever the node held, which was the finished line. State now survives
hide/show and is cleared only when the addon finalises or the plugin is disabled.

### Deliberate completion

Completing a line from the pad used to be a single-frame edge. With the guard back to
string comparison the retype is gone, so nothing in the completion path has to reason
about input timing. The press is consumed once, so a button still held when the next line
opens cannot finish that line on its first frame.

`HoldToCompleteButton` (on by default, 350 ms, configurable) makes the pad button
deliberate: the press is tracked from its `Pressed` and `Released` edges, because this
Dalamud exposes no held-state member, and the line completes only once the hold
threshold is met. A tap in a cutscene does nothing. The keyboard key stays a single
press.

Press detection now uses `IGamepadState.Raw`, which is the only member that reports a
button being held; `Pressed` and `Released` are single-frame edges and both are
documented as unreliable while gamepad UI navigation is active. A completion also latches
until the button is released, so holding Confirm through an advance cannot fill the line
that follows. That makes South / A a supported bind: the press finishes the line and the
same press advances it, which is the combination that reads best in sequential dialogue.

Completion itself is opt-in from here: the key and the button both default to none, and
a config migration clears a bind saved by an older build so an existing install keeps
quiet pacing rather than a bind it never chose. Pacing alone is the behaviour that
cannot surprise the player, because no press is ever reinterpreted - and binding
Confirm can never do better than that, since the game advances on the same press before
the plugin's hold requirement is even reached.

The command surface is two names and one grammar: `/talktypewriter` (or `/ttw`) with no
argument opens the window, a bare number sets the speed, and `on`, `off` and
`instant on|off` switch state. The pacer reads the configuration every frame, so a speed
change lands on the line already being revealed and on everything after it.

With the reveal measurements settled, the two debug probes and their settings checkboxes
were removed from the shipped plugin, along with the per-line trace logging. The
measurements they produced are recorded above and in the session log.
