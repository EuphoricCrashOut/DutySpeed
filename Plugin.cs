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
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ImGui = Dalamud.Bindings.ImGui.ImGui;
using ImGuiWindowFlags = Dalamud.Bindings.ImGui.ImGuiWindowFlags;
using Dalamud.Configuration;

namespace DutySpeed;

// --- DATA CLASSES ---
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
    public Guid Id { get; set; } = Guid.NewGuid(); // Added Unique ID for precise deletion
    public string Name { get; set; } = string.Empty;
    public TimeSpan Time { get; set; }
    public DateTime Date { get; set; }
    public List<PartyMember> Party { get; set; } = new();
}

// --- MAIN PLUGIN ---
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
    public HashSet<uint> DefeatedBossIds { get; } = new();

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

        CommandManager.AddHandler("/ds", new Dalamud.Game.Command.CommandInfo((_, _) => timerWindow.IsOpen = !timerWindow.IsOpen)
        {
            HelpMessage = "Toggles the DutySpeed timer window."
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (DutyState.IsDutyStarted)
        {
            var territory = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(ClientState.TerritoryType);
            var name = territory?.PlaceName.Value.Name.ExtractText() ?? "Unknown Duty";

            CurrentDutyName = name;
            cachedDutyName = name;
        }
        else
        {
            CurrentDutyName = "Not in Duty";
        }

        if (DutyState.IsDutyStarted && !IsRunning) StartDuty();
        if (!DutyState.IsDutyStarted && IsRunning) EndDuty();
        if (IsRunning) CheckBossDeaths();
    }

    private void StartDuty()
    {
        DutyTimer.Restart();
        IsRunning = true;
        DefeatedBossIds.Clear();
        SelectedHistoryDuty = cachedDutyName;
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

    private List<PartyMember> GetCurrentParty()
    {
        var members = new List<PartyMember>();
        var localPlayer = ObjectTable.LocalPlayer;

        if (PartyList.Length == 0 && localPlayer != null)
        {
            members.Add(new PartyMember
            {
                Name = localPlayer.Name.TextValue,
                Job = localPlayer.ClassJob.Value.Abbreviation.ExtractText()
            });
        }
        else
        {
            foreach (var member in PartyList)
            {
                members.Add(new PartyMember
                {
                    Name = member.Name.TextValue,
                    Job = member.ClassJob.Value.Abbreviation.ExtractText()
                });
            }
        }
        return members;
    }

    private void CheckBossDeaths()
    {
        foreach (var obj in ObjectTable)
        {
            if (obj is ICharacter { CurrentHp: 0 } character && !DefeatedBossIds.Contains(character.EntityId))
            {
                if (character.StatusFlags.HasFlag(StatusFlags.Hostile))
                {
                    DefeatedBossIds.Add(character.EntityId);
                }
            }
        }
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler("/ds");
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        windowSystem.RemoveAllWindows();
    }
}

// --- UI WINDOW ---
public class TimerWindow : Window
{
    private readonly Plugin plugin;
    private bool showHiddenSelection = false;
    private Guid? deleteConfirmId = null; // Track which record is pending deletion

    public TimerWindow(Plugin plugin) : base("DutySpeed Timer###DutySpeedMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(250, 200), MaximumSize = new Vector2(400, 800) };
        this.Flags = ImGuiWindowFlags.AlwaysAutoResize;
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
        ImGui.TextDisabled(plugin.IsRunning ? "Active Duty:" : "Status:");
        ImGui.Text(plugin.CurrentDutyName);

        var time = plugin.DutyTimer.Elapsed;
        ImGui.SetWindowFontScale(2.0f);
        ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"{time:mm\\:ss}");
        ImGui.SetWindowFontScale(1.0f);

        ImGui.Separator();
        ImGui.Text("Browse Records:");

        if (plugin.IsRunning) ImGui.BeginDisabled();

        var uniqueDuties = plugin.Config.RunHistory
            .Select(r => r.Name)
            .Distinct()
            .Where(name => showHiddenSelection || !plugin.Config.HiddenDuties.Contains(name))
            .ToList();

        if (string.IsNullOrEmpty(plugin.SelectedHistoryDuty) && uniqueDuties.Count > 0)
            plugin.SelectedHistoryDuty = uniqueDuties[0];

        ImGui.PushItemWidth(ImGui.GetWindowWidth() * 0.65f);
        if (ImGui.BeginCombo("##DutySelector", plugin.SelectedHistoryDuty))
        {
            foreach (var duty in uniqueDuties)
            {
                bool isHidden = plugin.Config.HiddenDuties.Contains(duty);
                if (ImGui.Selectable(isHidden ? $"[H] {duty}" : duty, plugin.SelectedHistoryDuty == duty))
                    plugin.SelectedHistoryDuty = duty;
            }
            ImGui.EndCombo();
        }
        ImGui.PopItemWidth();

        if (!string.IsNullOrEmpty(plugin.SelectedHistoryDuty))
        {
            ImGui.SameLine();
            bool currentlyHidden = plugin.Config.HiddenDuties.Contains(plugin.SelectedHistoryDuty);
            if (ImGui.Button(currentlyHidden ? "Unhide" : "Hide"))
            {
                if (currentlyHidden) plugin.Config.HiddenDuties.Remove(plugin.SelectedHistoryDuty);
                else plugin.Config.HiddenDuties.Add(plugin.SelectedHistoryDuty);
                plugin.Config.Save();
            }
        }

        ImGui.Checkbox("Show Hidden", ref showHiddenSelection);
        if (plugin.IsRunning) ImGui.EndDisabled();

        // --- RECORDS DISPLAY WITH DELETE BUTTON ---
        if (!string.IsNullOrEmpty(plugin.SelectedHistoryDuty))
        {
            var history = plugin.Config.RunHistory
                .Where(r => r.Name == plugin.SelectedHistoryDuty)
                .OrderBy(r => r.Time)
                .Take(5)
                .ToList();

            if (history.Any())
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0.2f, 1), $"Top 5 Records:");
                foreach (var run in history)
                {
                    // Logic for the Delete Button
                    if (ImGui.Button($"X##{run.Id}"))
                    {
                        if (deleteConfirmId == run.Id)
                        {
                            plugin.Config.RunHistory.RemoveAll(r => r.Id == run.Id);
                            plugin.Config.Save();
                            deleteConfirmId = null;
                        }
                        else
                        {
                            deleteConfirmId = run.Id;
                        }
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(deleteConfirmId == run.Id ? "Click again to confirm delete" : "Delete this record");

                    ImGui.SameLine();
                    ImGui.Text($"{run.Time:mm\\:ss} ({run.Date:MM/dd})");

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Party Composition:");
                        foreach (var m in run.Party) ImGui.Text($"[{m.Job}] {m.Name}");
                        ImGui.EndTooltip();
                    }
                }
            }
        }
    }
}