using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;

namespace TalkTypewriter.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    private static readonly (VirtualKey Key, string Label)[] CompleteKeyOptions =
    {
        ((VirtualKey)0, "None"),
        (VirtualKey.NUMPAD5, "Numpad 5"),
        (VirtualKey.NUMPAD1, "Numpad 1"),
        (VirtualKey.NUMPAD2, "Numpad 2"),
        (VirtualKey.NUMPAD3, "Numpad 3"),
        (VirtualKey.NUMPAD4, "Numpad 4"),
        (VirtualKey.NUMPAD6, "Numpad 6"),
        (VirtualKey.NUMPAD7, "Numpad 7"),
        (VirtualKey.NUMPAD8, "Numpad 8"),
        (VirtualKey.NUMPAD9, "Numpad 9"),
        (VirtualKey.F1, "F1"),
        (VirtualKey.F2, "F2"),
        (VirtualKey.F3, "F3"),
        (VirtualKey.F4, "F4"),
    };

    private static readonly string[] CompleteKeyLabels = BuildCompleteKeyLabels();

    private static readonly (GamepadButtons Button, string Label)[] CompleteButtonOptions =
    {
        (GamepadButtons.None, "None"),
        (GamepadButtons.South, "South / A (Confirm - also advances)"),
        (GamepadButtons.North, "North / Y"),
        (GamepadButtons.West, "West / X"),
        (GamepadButtons.East, "East / B"),
        (GamepadButtons.L1, "L1"),
        (GamepadButtons.R1, "R1"),
        (GamepadButtons.L2, "L2"),
        (GamepadButtons.R2, "R2"),
        (GamepadButtons.L3, "L3"),
        (GamepadButtons.R3, "R3"),
        (GamepadButtons.Start, "Start / Options"),
        (GamepadButtons.Select, "Select / Back"),
    };

    private static readonly string[] CompleteButtonLabels = BuildCompleteButtonLabels();

    private string newNpcName = string.Empty;
    private float newNpcSpeed = 35f;

    private static string[] BuildCompleteKeyLabels()
    {
        var labels = new string[CompleteKeyOptions.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            labels[i] = CompleteKeyOptions[i].Label;
        }

        return labels;
    }

    private static string[] BuildCompleteButtonLabels()
    {
        var labels = new string[CompleteButtonOptions.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            labels[i] = CompleteButtonOptions[i].Label;
        }

        return labels;
    }

    public ConfigWindow(Plugin plugin) : base("TalkTypewriter Settings###TalkTypewriterConfig")
    {
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 200),
            MaximumSize = new Vector2(900, 900)
        };

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enable text pacing", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!configuration.Enabled)
        {
            ImGui.TextDisabled("Pacing is disabled - dialogue text uses the native reveal.");
            return;
        }

        var instant = configuration.InstantText;
        if (ImGui.Checkbox("Reveal instantly (native behavior)", ref instant))
        {
            configuration.InstantText = instant;
            configuration.Save();
        }

        var speed = configuration.SpeedCps;
        if (ImGui.SliderFloat("Characters per second (global)", ref speed, 1f, 200f, "%.0f"))
        {
            configuration.SpeedCps = speed;
            configuration.Save();
        }

        ImGui.TextDisabled($"Effective line duration at {speed:0} cps: ~{(speed > 0 ? 2.5f * 100f / speed : 0):0.0}s for a 2.5-line box");

        var keyIndex = Array.FindIndex(CompleteKeyOptions, o => o.Key == configuration.CompleteKey);
        if (keyIndex < 0)
        {
            keyIndex = 1;
        }

        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Complete-line key", ref keyIndex, CompleteKeyLabels, CompleteKeyLabels.Length))
        {
            configuration.CompleteKey = CompleteKeyOptions[keyIndex].Key;
            configuration.Save();
        }

        ImGui.TextDisabled("Finishes the current line without advancing. Space, Enter and Numpad 0 stay with the game.");

        var buttonIndex = Array.FindIndex(CompleteButtonOptions, o => o.Button == configuration.CompleteButton);
        if (buttonIndex < 0)
        {
            buttonIndex = 0;
        }

        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Complete-line button", ref buttonIndex, CompleteButtonLabels, CompleteButtonLabels.Length))
        {
            configuration.CompleteButton = CompleteButtonOptions[buttonIndex].Button;
            configuration.Save();
        }

        ImGui.TextDisabled("Reads the game's own pad state. Confirm can be bound, but the game advances on that same press, so the rest of the line is skipped rather than shown.");

        if (configuration.CompleteButton != GamepadButtons.None)
        {
            var hold = configuration.HoldToCompleteButton;
            if (ImGui.Checkbox("Require holding the button", ref hold))
            {
                configuration.HoldToCompleteButton = hold;
                configuration.Save();
            }

            if (configuration.HoldToCompleteButton)
            {
                var holdMs = configuration.HoldToCompleteMs;
                ImGui.SetNextItemWidth(180f);
                if (ImGui.SliderFloat("Hold time (ms)", ref holdMs, 150f, 1000f, "%.0f"))
                {
                    configuration.HoldToCompleteMs = holdMs;
                    configuration.Save();
                }

                ImGui.TextDisabled("The button only completes the line once held this long, so a tap in a cutscene does nothing. The keyboard key is unaffected.");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Per-NPC speed overrides");

        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("NPC name", ref newNpcName, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputFloat("chars/sec", ref newNpcSpeed);

        ImGui.SameLine();
        if (ImGui.Button("Add override") && !string.IsNullOrWhiteSpace(newNpcName))
        {
            configuration.NpcSpeeds[newNpcName.Trim().ToLowerInvariant()] = Math.Max(1f, newNpcSpeed);
            configuration.Save();
            newNpcName = string.Empty;
        }

        ImGui.Spacing();

        if (configuration.NpcSpeeds.Count == 0)
        {
            ImGui.TextDisabled("No overrides yet. Add one to give a specific NPC its own typing speed.");
        }
        else
        {
            var toRemove = new List<string>();
            var index = 0;
            foreach (var kv in configuration.NpcSpeeds)
            {
                ImGui.Text($"{kv.Key}: {kv.Value:0.#} cps");
                ImGui.SameLine();
                if (ImGui.Button($"Remove##npc{index++}"))
                {
                    toRemove.Add(kv.Key);
                }
            }

            if (toRemove.Count > 0)
            {
                foreach (var key in toRemove)
                {
                    configuration.NpcSpeeds.Remove(key);
                }

                configuration.Save();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        var info = plugin.Pacing.StatusLine;
        ImGui.TextDisabled(string.IsNullOrEmpty(info) ? "No dialogue seen yet this session." : info);
    }
}