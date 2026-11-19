using System;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TalkTypewriter;

/// <summary>
/// Paces a Talk line by writing prefixes into the game's own text node with
/// <c>AtkTextNode.SetText</c>. The game keeps drawing the font, wrap, edge and box
/// art itself, so nothing is drawn over the box and the node is never hidden.
///
/// Measured behaviour this relies on (PLAN.md section 14):
///   - the game writes the whole line in one frame and never rewrites it mid-line,
///     so there is no native reveal to retime;
///   - no tween targets the text node, so the reveal is not an AtkSimpleTween;
///   - a full line already wraps at draw time with the native flags, so TextFlags
///     geometry are never touched;
///   - ApplyTextFlow must never be called: SetText points OriginalTextPointer at
///     the text it just wrote, so ApplyTextFlow reflows source over destination and
///     erases the node.
///
/// Timing is owned by the addon's PreDraw event, which fires immediately before
/// AtkUnitBase.Draw. Framework.Update can land after the draw, which showed the full
/// line for one frame; PreDraw cannot, so the prefix is always what gets drawn.
/// </summary>
public unsafe class TalkPacing : IDisposable
{
    private const string TalkAddonName = "Talk";
    private const uint TextNodeId = 3;
    private const uint NameNodeId = 2;

    /// <summary>Above this speed the line is simply left alone (native).</summary>
    private const float InstantThreshold = 100000f;

    private const byte SeStringStart = 0x02;
    private const byte SeStringEnd = 0x03;

    private readonly Configuration config;

    private AtkTextNode* dialogueNode;
    private string speakerName = string.Empty;

    // The whole line as UTF-8, plus one cut offset per revealable character.
    private byte[] fullBytes = Array.Empty<byte>();
    private string fullText = string.Empty;
    private int[] cutOffsets = Array.Empty<int>();
    private int totalChars;

    // What is currently in the node, so refreshes of the same line are recognised.
    // Node contents are compared as strings; nothing depends on Utf8String.Length.
    private int writtenBytes = -1;
    private byte[] writtenBuffer = Array.Empty<byte>();
    private string writtenText = string.Empty;
    private int shownChars;

    // Hold tracking for the gamepad complete button, so a stray touch in a cutscene
    // cannot finish a line while the hold requirement is on.
    private bool completeButtonHeld;
    private bool completeButtonSpent;
    private DateTime completeButtonDownAt = DateTime.MinValue;

    private DateTime lineStarted;
    private bool active;

    public string StatusLine { get; private set; } = string.Empty;

    public TalkPacing(Configuration config)
    {
        this.config = config;

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, TalkAddonName, OnTalkSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, TalkAddonName, OnTalkRefresh);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, TalkAddonName, OnTalkFinalize);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, TalkAddonName, OnTalkPreDraw);
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, TalkAddonName, OnTalkSetup);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, TalkAddonName, OnTalkRefresh);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, TalkAddonName, OnTalkFinalize);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, TalkAddonName, OnTalkPreDraw);
        Plugin.Framework.Update -= OnFrameworkUpdate;

        RestoreNative();
        Clear();
    }

    private void OnTalkSetup(AddonEvent type, AddonArgs args) => TryStart(GetTalkAddon());

    /// <summary>
    /// The one event that carries a line change. A repaint caused by input or a HUD
    /// change never reaches this event, so identical text arriving here is a new line
    /// that repeats the previous one rather than the finished line coming back.
    /// </summary>
    private void OnTalkRefresh(AddonEvent type, AddonArgs args) => TryStart(GetTalkAddon(), lineChange: true);

    private void OnTalkFinalize(AddonEvent type, AddonArgs args) => Clear();

    /// <summary>
    /// Runs immediately before the addon draws, which is the only moment where a
    /// write is guaranteed to be on screen this frame. The game sets a new line
    /// during its update, so by the time this fires the node holds the full text and
    /// is replaced here before the frame is rendered.
    /// </summary>
    private void OnTalkPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!config.Enabled)
        {
            return;
        }

        if (!active || dialogueNode == null)
        {
            TryStart(GetTalkAddon());
            if (!active || dialogueNode == null)
            {
                return;
            }
        }
        else if (!IsLive(dialogueNode))
        {
            Clear();
            return;
        }

        EnsurePacedTextBeforeDraw();

        if (dialogueNode == null || shownChars >= totalChars)
        {
            return;
        }

        if (config.InstantText || EffectiveSpeed() >= InstantThreshold)
        {
            WritePrefix(totalChars, force: true);
            return;
        }

        var elapsed = (DateTime.UtcNow - lineStarted).TotalSeconds;
        var target = (int)(elapsed * EffectiveSpeed());
        if (target > shownChars)
        {
            WritePrefix(Math.Min(totalChars, target), force: false);
        }
    }

    /// <summary>
    /// Starts pacing a new line, or keeps the current one when the addon repaints
    /// mid-line. Talk hides and shows while a conversation plays, so the pacer's state
    /// is deliberately kept across hide/show: clearing it made the next pre-draw start
    /// the finished line over, which is the repeated text that was reported.
    /// </summary>
    private void TryStart(AtkUnitBase* addon, bool lineChange = false)
    {
        if (addon == null)
        {
            return;
        }

        if (!config.Enabled)
        {
            if (active)
            {
                RestoreNative();
                Clear();
            }

            return;
        }

        var node = PickTextNode(addon);
        if (node == null)
        {
            return;
        }

        var raw = ReadBytes(node);
        if (raw.Length == 0)
        {
            return;
        }

        if (active && node == dialogueNode)
        {
            if (BytesEqual(raw, writtenBuffer))
            {
                // A refresh that saw our own prefix: still the same line.
                return;
            }

            if (BytesEqual(raw, fullBytes))
            {
                // The whole current line came back. Mid-reveal that is a repaint, so
                // the clock is kept. On a finished line, a line-change event carrying
                // this exact text can only be a new line that repeats the previous
                // one word for word.
                if (lineChange && shownChars >= totalChars)
                {
                    Plugin.Log.Debug("[TalkTypewriter] identical line on a refresh; pacing it again");
                    StartLine(node, raw);
                    return;
                }

                Plugin.Log.Debug($"[TalkTypewriter] refresh put the full line back at {shownChars}/{totalChars}; re-applied prefix");
                WritePrefix(Math.Max(1, shownChars), force: true);
                return;
            }

            Plugin.Log.Debug($"[TalkTypewriter] refresh carried a new line ({raw.Length} bytes); starting it");
        }

        StartLine(node, raw);
    }

    private void StartLine(AtkTextNode* node, byte[] raw)
    {
        dialogueNode = node;
        fullBytes = raw;
        fullText = Encoding.UTF8.GetString(raw);
        cutOffsets = BuildCutOffsets(raw);
        totalChars = cutOffsets.Length;
        writtenBytes = -1;
        writtenBuffer = Array.Empty<byte>();
        writtenText = string.Empty;
        shownChars = 0;
        lineStarted = DateTime.UtcNow;
        active = true;
        speakerName = PickSpeakerName(GetTalkAddon());

        var instant = config.InstantText || EffectiveSpeed() >= InstantThreshold;
        if (instant || totalChars == 0)
        {
            WritePrefix(totalChars, force: true);
        }
        else
        {
            // Same frame the game set the line, so the full line is never shown.
            WritePrefix(1, force: true);
        }

        Plugin.Log.Debug(
            $"[TalkTypewriter] line start {totalChars} chars | speaker='{speakerName}' | cps={EffectiveSpeed():0.#}" +
            (instant ? " | instant" : string.Empty));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!config.Enabled)
        {
            if (active)
            {
                RestoreNative();
                Clear();
            }

            return;
        }

        if (!active || dialogueNode == null)
        {
            return;
        }

        if (!IsLive(dialogueNode))
        {
            Clear();
            return;
        }

        if (shownChars >= totalChars)
        {
            return;
        }

        // The reveal advances in PreDraw, which is the only point guaranteed to run
        // before the frame is rendered. This handles the skip request only.
        if (CompleteRequested())
        {
            WritePrefix(totalChars, force: true);
        }
    }

    /// <summary>
    /// Pre-draw safety net, called from the addon's PreDraw event. If the node no
    /// longer holds what we last wrote, either the game put the full current line back
    /// (re-apply the prefix, keep the clock running) or a different line arrived
    /// (start it now, so this frame draws its first character instead of the line).
    ///
    /// This cannot loop: starting a line writes its one-character prefix immediately,
    /// so the next check finds our own text and returns.
    /// </summary>
    private void EnsurePacedTextBeforeDraw()
    {
        if (dialogueNode == null || totalChars == 0 || writtenText.Length == 0)
        {
            return;
        }

        var current = dialogueNode->NodeText.ToString();
        if (current.Length == 0)
        {
            return;
        }

        if (current == writtenText)
        {
            // Still our own prefix, or the same line put back by a refresh.
            return;
        }

        if (current == fullText)
        {
            // The same line with the full text put back: re-apply, keep the clock.
            Plugin.Log.Debug($"[TalkTypewriter] pre-draw restored the full line at {shownChars}/{totalChars}; re-applied prefix");
            WritePrefix(Math.Max(1, shownChars), force: true);
            return;
        }

        // A different line arrived without a refresh event.
        StartLine(dialogueNode, Encoding.UTF8.GetBytes(current));
    }

    private void WritePrefix(int chars, bool force)
    {
        if (!active || dialogueNode == null || totalChars <= 0)
        {
            return;
        }

        chars = Math.Clamp(chars, 1, totalChars);
        var cut = cutOffsets[chars - 1];
        if (!force && cut == writtenBytes)
        {
            shownChars = chars;
            return;
        }

        // AtkTextNode.SetText forwards the span to native code that expects a
        // null-terminated buffer and does not append one itself.
        var buffer = new byte[cut + 1];
        Array.Copy(fullBytes, buffer, cut);
        buffer[cut] = 0;

        try
        {
            dialogueNode->SetText(new ReadOnlySpan<byte>(buffer, 0, cut + 1));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[TalkTypewriter] SetText failed; leaving the native line alone");
            Clear();
            return;
        }

        writtenBytes = cut;
        writtenBuffer = new byte[cut];
        Array.Copy(fullBytes, writtenBuffer, cut);
        writtenText = Encoding.UTF8.GetString(writtenBuffer);
        shownChars = chars;

        if (chars >= totalChars)
        {
            Plugin.Log.Debug(
                $"[TalkTypewriter] line complete after {(DateTime.UtcNow - lineStarted).TotalSeconds:0.00}s");
        }

        UpdateStatus();
    }

    private void RestoreNative()
    {
        if (!active || dialogueNode == null || fullBytes.Length == 0 || !IsLive(dialogueNode))
        {
            return;
        }

        var buffer = new byte[fullBytes.Length + 1];
        Array.Copy(fullBytes, buffer, fullBytes.Length);
        buffer[fullBytes.Length] = 0;

        try
        {
            dialogueNode->SetText(new ReadOnlySpan<byte>(buffer, 0, fullBytes.Length + 1));
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "[TalkTypewriter] could not restore the native line");
        }
    }

    private void Clear()
    {
        dialogueNode = null;
        fullBytes = Array.Empty<byte>();
        fullText = string.Empty;
        cutOffsets = Array.Empty<int>();
        totalChars = 0;
        writtenBytes = -1;
        writtenBuffer = Array.Empty<byte>();
        writtenText = string.Empty;
        shownChars = 0;
        completeButtonHeld = false;
        completeButtonDownAt = DateTime.MinValue;
        active = false;
    }

    /// <summary>
    /// Splits the raw bytes into one step per visible character. Multi-byte UTF-8 is
    /// kept whole, and SeString payloads (bracketed by 0x02 .. 0x03) are revealed
    /// atomically so markup is never cut in half.
    /// </summary>
    private static int[] BuildCutOffsets(byte[] raw)
    {
        var cuts = new int[raw.Length];
        var count = 0;
        var i = 0;

        while (i < raw.Length)
        {
            if (raw[i] == SeStringStart)
            {
                var end = -1;
                for (var j = i + 1; j < raw.Length; j++)
                {
                    if (raw[j] == SeStringEnd)
                    {
                        end = j;
                        break;
                    }
                }

                i = end >= 0 ? end + 1 : raw.Length;
            }
            else if (raw[i] < 0x80)
            {
                i += 1;
            }
            else if ((raw[i] & 0xE0) == 0xC0)
            {
                i += 2;
            }
            else if ((raw[i] & 0xF0) == 0xE0)
            {
                i += 3;
            }
            else if ((raw[i] & 0xF8) == 0xF0)
            {
                i += 4;
            }
            else
            {
                // Stray continuation byte: take it alone rather than run past the end.
                i += 1;
            }

            if (i > raw.Length)
            {
                i = raw.Length;
            }

            cuts[count++] = i;
        }

        var result = new int[count];
        Array.Copy(cuts, result, count);
        return result;
    }

    private static byte[] ReadBytes(AtkTextNode* node)
    {
        var text = node->NodeText.ToString();
        return text.Length == 0 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// The dialogue line is node 3 (stable in every measured line); the longest text
    /// node is the fallback if that ever changes.
    /// </summary>
    private static AtkTextNode* PickTextNode(AtkUnitBase* addon)
    {
        var nodeList = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;

        AtkTextNode* byId = null;
        AtkTextNode* longest = null;
        var longestLength = 0;

        for (var i = 0; i < count; i++)
        {
            var res = nodeList[i];
            if (res == null || res->Type != NodeType.Text)
            {
                continue;
            }

            var textNode = (AtkTextNode*)res;
            var length = textNode->NodeText.ToString().Length;
            if (length == 0)
            {
                continue;
            }

            if (res->NodeId == TextNodeId)
            {
                byId = textNode;
            }

            if (length > longestLength)
            {
                longest = textNode;
                longestLength = length;
            }
        }

        return byId != null ? byId : longest;
    }

    /// <summary>The speaker name is node 2; empty when the line has no name node.</summary>
    private static string PickSpeakerName(AtkUnitBase* addon)
    {
        if (addon == null)
        {
            return string.Empty;
        }

        var nodeList = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;

        for (var i = 0; i < count; i++)
        {
            var res = nodeList[i];
            if (res == null || res->Type != NodeType.Text || res->NodeId != NameNodeId)
            {
                continue;
            }

            var text = ((AtkTextNode*)res)->NodeText.ToString().Trim();
            if (text.Length > 0 && text.Length <= 80)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static bool IsLive(AtkTextNode* node)
    {
        var addon = GetTalkAddon();
        if (addon == null)
        {
            return false;
        }

        var nodeList = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            if (nodeList[i] == (AtkResNode*)node)
            {
                return true;
            }
        }

        return false;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the player asks for the rest of the line, from either the keyboard key
    /// or the gamepad button. A pad press completes at most one line: the button has to
    /// be released before it can complete another, so holding Confirm through an advance
    /// cannot also fill the line that follows.
    /// </summary>
    private bool CompleteRequested()
    {
        if ((int)config.CompleteKey != 0 && Plugin.KeyState[config.CompleteKey])
        {
            return true;
        }

        var button = config.CompleteButton;
        if (button == GamepadButtons.None)
        {
            return false;
        }

        UpdateButtonHold(button);
        if (!completeButtonHeld || completeButtonSpent)
        {
            return false;
        }

        var holdNeeded = config.HoldToCompleteButton ? Math.Max(0f, config.HoldToCompleteMs) : 0f;
        if ((DateTime.UtcNow - completeButtonDownAt).TotalMilliseconds < holdNeeded)
        {
            return false;
        }

        completeButtonHeld = false;
        completeButtonSpent = true;
        return true;
    }

    /// <summary>
    /// Tracks the held state of the complete-line button. Raw is used because it is the
    /// only member that reports a button being held: Pressed and Released are single-frame
    /// edges and are documented as unreliable while gamepad UI navigation is active, which
    /// is why a held Confirm could appear to release and re-press and fill the next line.
    /// </summary>
    private void UpdateButtonHold(GamepadButtons button)
    {
        if (Plugin.GamepadState.Raw(button) <= 0.5f)
        {
            completeButtonHeld = false;
            completeButtonDownAt = DateTime.MinValue;
            completeButtonSpent = false;
            return;
        }

        if (completeButtonSpent || completeButtonHeld)
        {
            return;
        }

        completeButtonHeld = true;
        completeButtonDownAt = DateTime.UtcNow;
    }

    private float EffectiveSpeed()
    {
        if (!string.IsNullOrEmpty(speakerName) &&
            config.NpcSpeeds.TryGetValue(speakerName.ToLowerInvariant(), out var npcSpeed) &&
            npcSpeed > 0f)
        {
            return npcSpeed;
        }

        return Math.Max(1f, config.SpeedCps);
    }

    private void UpdateStatus()
    {
        var who = string.IsNullOrEmpty(speakerName) ? "Line" : speakerName;
        StatusLine = $"{who} - {shownChars}/{totalChars} chars @ {EffectiveSpeed():0.#} cps";
    }

    private static AtkUnitBase* GetTalkAddon()
    {
        var addonPtr = Plugin.GameGui.GetAddonByName(TalkAddonName);
        return addonPtr.Address == 0 ? null : (AtkUnitBase*)addonPtr.Address;
    }
}
