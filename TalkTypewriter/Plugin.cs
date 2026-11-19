using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TalkTypewriter.Windows;

namespace TalkTypewriter;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IGamepadState GamepadState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/talktypewriter";
    private const string ShortCommandName = "/ttw";

    /// <summary>Same range as the speed slider, so chat and window agree.</summary>
    private const float MinSpeed = 1f;
    private const float MaxSpeed = 200f;

    private const string Usage =
        "settings: /ttw | speed: /ttw 90 | on/off: /ttw on | instant: /ttw instant on";

    public Configuration Configuration { get; init; }
    public TalkPacing Pacing { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("TalkTypewriter");
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        Pacing = new TalkPacing(Configuration);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open TalkTypewriter settings, or set the speed with an argument: /talktypewriter 90"
        });

        CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short form of /talktypewriter: /ttw 90, /ttw on, /ttw off, /ttw instant on"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // The settings window is the plugin's only UI, so it doubles as the main entry
        // point the installer offers. Without a subscriber here Dalamud flags the plugin
        // as having no main UI callback.
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        Log.Information("TalkTypewriter loaded. Use /talktypewriter or /ttw to open settings.");
    }

    public void Dispose()
    {
        Pacing.Dispose();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommandName);
    }

    /// <summary>
    /// Shared by both command names. No argument opens the window, so old muscle memory
    /// still works; an argument changes the live configuration, which is what makes the
    /// speed adjustable mid-conversation and mid-cutscene.
    /// </summary>
    private void OnCommand(string command, string args)
    {
        var text = args.Trim();
        if (text.Length == 0)
        {
            ToggleConfigUi();
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "on":
                SetEnabled(true);
                return;
            case "off":
                SetEnabled(false);
                return;
            case "ui":
            case "settings":
            case "config":
                ToggleConfigUi();
                return;
            case "instant":
                SetInstant(parts.Length > 1
                    ? ParseFlag(parts[1], !Configuration.InstantText)
                    : !Configuration.InstantText);
                return;
        }

        // The common case: /ttw 90.
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            SetSpeed(speed);
            return;
        }

        ChatGui.Print($"[TalkTypewriter] {Usage}");
    }

    private void SetSpeed(float speed)
    {
        var clamped = Math.Clamp(speed, MinSpeed, MaxSpeed);
        Configuration.SpeedCps = clamped;
        Configuration.Save();

        var notes = new List<string>();
        if (clamped != speed)
        {
            notes.Add($"clamped from {speed:0.#}");
        }

        if (Configuration.InstantText)
        {
            notes.Add("reveal instantly is on");
        }

        if (Configuration.NpcSpeeds.Count > 0)
        {
            notes.Add("NPC overrides still apply");
        }

        var suffix = notes.Count == 0 ? string.Empty : $" ({string.Join(", ", notes)})";
        ChatGui.Print($"[TalkTypewriter] {clamped:0.#} cps{suffix}");
    }

    private void SetEnabled(bool enabled)
    {
        Configuration.Enabled = enabled;
        Configuration.Save();
        ChatGui.Print(enabled
            ? "[TalkTypewriter] pacing on."
            : "[TalkTypewriter] pacing off; dialogue uses the native reveal.");
    }

    private void SetInstant(bool instant)
    {
        Configuration.InstantText = instant;
        Configuration.Save();
        ChatGui.Print(instant
            ? "[TalkTypewriter] lines reveal instantly."
            : "[TalkTypewriter] lines reveal at the configured speed.");
    }

    private static bool ParseFlag(string value, bool fallback) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => fallback,
    };

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
