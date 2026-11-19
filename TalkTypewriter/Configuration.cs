using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;

namespace TalkTypewriter;

[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>Bumped when a saved config needs a one-time correction.</summary>
    private const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Master toggle for the typewriter pacing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Characters revealed per second for the global default.</summary>
    public float SpeedCps { get; set; } = 35f;

    /// <summary>When true, dialogue text is revealed instantly (native behavior).</summary>
    public bool InstantText { get; set; } = false;

    /// <summary>
    /// Key that completes the current line without advancing it. Off by default:
    /// completion is opt-in, because pacing on its own is the predictable behaviour.
    /// Never Space, Return or Numpad0: the game consumes those to advance, so binding
    /// one would compete with the native advance input.
    /// </summary>
    public VirtualKey CompleteKey { get; set; } = VirtualKey.NO_KEY;

    /// <summary>
    /// Gamepad button that completes the current line, read from the game's own pad
    /// state. Off by default. R3 and L3 are unbound in normal play; Confirm
    /// (South / Cross / A) can be selected, but the game also advances on that press,
    /// so the rest of the line is skipped rather than shown.
    /// </summary>
    public GamepadButtons CompleteButton { get; set; } = GamepadButtons.None;

    /// <summary>
    /// When true, the gamepad button has to be held for HoldToCompleteMs before it
    /// completes a line, so a stray touch during a cutscene cannot finish it. Only the
    /// pad is affected; the keyboard key stays a single press.
    /// </summary>
    public bool HoldToCompleteButton { get; set; } = true;

    /// <summary>How long the complete-line button must be held, in milliseconds.</summary>
    public float HoldToCompleteMs { get; set; } = 350f;

    /// <summary>Per-NPC speed overrides, keyed by lowercased speaker name.</summary>
    public Dictionary<string, float> NpcSpeeds { get; set; } = new();

    /// <summary>
    /// Configs saved before version 1 defaulted completion to Numpad5 and R3. Now that
    /// completion is opt-in, an older config is cleared once, so an existing install
    /// gets the quiet behaviour instead of a bind it never chose. A choice made after
    /// this point is never overwritten.
    /// </summary>
    public void Migrate()
    {
        if (Version >= CurrentVersion)
        {
            return;
        }

        CompleteKey = VirtualKey.NO_KEY;
        CompleteButton = GamepadButtons.None;
        Version = CurrentVersion;
        Save();
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}