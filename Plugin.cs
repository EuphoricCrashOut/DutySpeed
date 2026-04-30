using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services; // API 15 Standard Namespace
using Dalamud.Utility;
using Dalamud.Configuration;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

// API 15 Hexa-based Bindings
using ImGui = Dalamud.Bindings.ImGui.ImGui;
using ImGuiWindowFlags = Dalamud.Bindings.ImGui.ImGuiWindowFlags;
using ImGuiCond = Dalamud.Bindings.ImGui.ImGuiCond;

namespace DutySpeed;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;
    public List<DutyRecord> RunHistory { get; set; } = new();
    public HashSet<string> HiddenDuties { get; set; } = new();
    public bool AutoOpenOnDuty { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

public class PartyMember
{
    public string Name { get; set; } = string.Empty;
    public string Job { get; set; } = string.Empty;
}

public class DutyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public TimeSpan Time { get; set; }
    public DateTime Date { get; set; }
    public List<PartyMember> Party { get; set; } = new();
}

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

    public Stopwatch DutyTimer { get; } = new();
    public bool IsRunning { get; private set; } = false;

    // API 15: Migration to 64-bit ulong for GameObject IDs
    public HashSet<ulong> DefeatedBossIds { get; } = new();

    public string CurrentDutyName { get; set; } = "Not in Duty";
    private string cachedDutyName = "Unknown Duty";
    public string SelectedHistoryDuty { get; set; } = string.Empty;

    public Configuration Config { get; }
    private readonly WindowSystem windowSystem = new("DutySpeed");
    private readonly TimerWindow timerWindow;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.HiddenDuties ??= new HashSet<string>();

        timerWindow = new TimerWindow(this);
        windowSystem.AddWindow(timerWindow);

        CommandManager.AddHandler("/ds", new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "Toggles the DutySpeed timer window."
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        Framework.Update += OnUpdate;

        DutyState.DutyStarted += OnDutyStarted;
        DutyState.DutyCompleted += OnDutyCompleted;
    }

    private void OnCommand(string command, string args) => timerWindow.IsOpen = !timerWindow.IsOpen;
    private void DrawUI() => windowSystem.Draw();

    private void OnUpdate(IFramework framework)
    {
        if (DutyState.IsDutyStarted)
        {
            var territory = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(ClientState.TerritoryType);
            if (territory.HasValue)
            {
                var name = territory.Value.PlaceName.Value.Name.ToString();
                CurrentDutyName = name;
                cachedDutyName = name;
            }
        }
        else
        {
            CurrentDutyName = "Not in Duty";
            if (IsRunning) StopTimerWithoutSaving();
        }

        if (IsRunning) CheckBossDeaths();
    }

    private void OnDutyStarted(object? sender, ushort territoryId)
    {
        if (!IsRunning) StartDuty();
    }

    private void OnDutyCompleted(object? sender, ushort territoryId)
    {
        if (IsRunning) EndDuty();
    }

    private void StartDuty()
    {
        var territory = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(ClientState.TerritoryType);
        cachedDutyName = territory?.PlaceName.Value.Name.ToString() ?? "Unknown Duty";
        CurrentDutyName = cachedDutyName;
        SelectedHistoryDuty = cachedDutyName;

        DutyTimer.Restart();
        IsRunning = true;
        DefeatedBossIds.Clear();

        if (Config.AutoOpenOnDuty) timerWindow.IsOpen = true;
    }

    private void EndDuty()
    {
        DutyTimer.Stop();
        IsRunning = false;

        if (DutyTimer.Elapsed.TotalSeconds > 10)
        {
            var record = new DutyRecord
            {
                Name = cachedDutyName,
                Time = DutyTimer.Elapsed,
                Date = DateTime.Now,
                Party = GetCurrentParty()
            };
            Config.RunHistory.Add(record);
            Config.HiddenDuties.Remove(cachedDutyName);
            Config.Save();
            SelectedHistoryDuty = cachedDutyName;
        }
    }

    private void StopTimerWithoutSaving()
    {
        DutyTimer.Stop();
        IsRunning = false;
        ChatGui.Print("[DutySpeed] Duty abandoned. Timer stopped.");
    }

    private List<PartyMember> GetCurrentParty()
    {
        var members = new List<PartyMember>();

        // API 15: IClientState.LocalPlayer is obsolete; use IObjectTable.LocalPlayer
        var localPlayer = ObjectTable.LocalPlayer;

        if (PartyList.Length == 0 && localPlayer != null)
        {
            members.Add(new PartyMember
            {
                Name = localPlayer.Name.TextValue,
                Job = localPlayer.ClassJob.Value.Abbreviation.ToString()
            });
        }
        else
        {
            foreach (var member in PartyList)
            {
                members.Add(new PartyMember
                {
                    Name = member.Name.TextValue,
                    Job = member.ClassJob.Value.Abbreviation.ToString()
                });
            }
        }
        return members;
    }

    private void CheckBossDeaths()
    {
        foreach (var obj in ObjectTable)
        {
            // API 15: Using GameObjectId (ulong) instead of EntityId (uint)
            if (obj is ICharacter character && character.CurrentHp == 0 && !DefeatedBossIds.Contains(character.GameObjectId))
            {
                if (character.StatusFlags.HasFlag(StatusFlags.Hostile))
                {
                    DefeatedBossIds.Add(character.GameObjectId);
                }
            }
        }
    }

    public void Dispose()
    {
        DutyState.DutyStarted -= OnDutyStarted;
        DutyState.DutyCompleted -= OnDutyCompleted;
        CommandManager.RemoveHandler("/ds");
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        windowSystem.RemoveAllWindows();
    }
}

public class TimerWindow : Window
{
    private readonly Plugin plugin;

    public TimerWindow(Plugin plugin) : base("DutySpeed Timer###DutySpeedMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(250, 160),
            MaximumSize = new Vector2(1000, 1000)
        };
        this.Size = new Vector2(300, 220);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var autoOpen = plugin.Config.AutoOpenOnDuty;
        if (ImGui.Checkbox("Auto-open in Duty", ref autoOpen))
        {
            plugin.Config.AutoOpenOnDuty = autoOpen;
            plugin.Config.Save();
        }

        ImGui.Separator();
        ImGui.Text(plugin.CurrentDutyName);

        var time = plugin.DutyTimer.Elapsed;
        ImGui.SetWindowFontScale(2.0f);
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"{time:mm\\:ss}");
        ImGui.SetWindowFontScale(1.0f);

        ImGui.Separator();

        if (!string.IsNullOrEmpty(plugin.SelectedHistoryDuty))
        {
            var history = plugin.Config.RunHistory
                .Where(r => r.Name == plugin.SelectedHistoryDuty)
                .OrderBy(r => r.Time)
                .Take(5)
                .ToList();

            foreach (var run in history)
            {
                ImGui.Text($"{run.Time:mm\\:ss} ({run.Date:MM/dd})");
                if (ImGui.IsItemHovered())
                {
                    using (ImRaii.Tooltip())
                    {
                        foreach (var m in run.Party) ImGui.Text($"[{m.Job}] {m.Name}");
                    }
                }
            }
        }
    }
}