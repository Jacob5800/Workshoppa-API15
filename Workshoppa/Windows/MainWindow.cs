using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using LLib;
using LLib.ImGui;
using Workshoppa.GameData;

namespace Workshoppa.Windows;

// FIXME The close button doesn't work near the workshop, either hide it or make it work
internal sealed class MainWindow : LWindow, IPersistableWindowConfig
{
    private static readonly Regex CountAndName = new(@"^(\d{1,5})x?\s+(.*)$", RegexOptions.Compiled);

    private readonly WorkshopPlugin _plugin;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly Configuration _configuration;
    private readonly WorkshopCache _workshopCache;
    private readonly IconCache _iconCache;
    private readonly IChatGui _chatGui;
    private readonly RecipeTree _recipeTree;
    private readonly IPluginLog _pluginLog;
    private readonly RetainerDepositWindow _retainerDepositWindow;

    private string _searchString = string.Empty;
    private bool _checkInventory;
    private string _newPresetName = string.Empty;

    public MainWindow(WorkshopPlugin plugin, IDalamudPluginInterface pluginInterface, IClientState clientState,
        IObjectTable objectTable, Configuration configuration, WorkshopCache workshopCache, IconCache iconCache, IChatGui chatGui,
        RecipeTree recipeTree, IPluginLog pluginLog, RetainerDepositWindow retainerDepositWindow)
        : base("Workshoppa###WorkshoppaMainWindow")
    {
        _plugin = plugin;
        _pluginInterface = pluginInterface;
        _clientState = clientState;
        _objectTable = objectTable;
        _configuration = configuration;
        _workshopCache = workshopCache;
        _iconCache = iconCache;
        _chatGui = chatGui;
        _recipeTree = recipeTree;
        _pluginLog = pluginLog;
        _retainerDepositWindow = retainerDepositWindow;

        Position = new Vector2(100, 100);
        PositionCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(350, 50),
            MaximumSize = new Vector2(500, 9999),
        };

        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.MenuBar;
        AllowClickthrough = false;
    }

    public EOpenReason OpenReason { get; set; } = EOpenReason.None;
    public bool NearFabricationStation { get; set; }
    public ButtonState State { get; set; } = ButtonState.None;

    private bool IsDiscipleOfHand =>
        _objectTable.LocalPlayer != null && _objectTable.LocalPlayer.ClassJob.RowId is >= 8 and <= 15;

    public WindowConfig WindowConfig => _configuration.MainWindowConfig;

    public override void DrawContent()
    {
        if (ImGui.BeginMenuBar())
        {
            ImGui.BeginDisabled(_plugin.CurrentStage != Stage.Stopped);
            DrawPresetsMenu();
            DrawClipboardMenu();
            ImGui.EndDisabled();
            if (ImGui.MenuItem("Retainer Deposit"))
                _retainerDepositWindow.ToggleFromMenu();

            ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.GetFrameHeightWithSpacing());
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Comments))
                Util.OpenLink("https://discord.gg/TTPZ82xaUd");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Join the Workshoppa Discord");

            ImGui.EndMenuBar();
        }

        var currentItem = _configuration.CurrentlyCraftedItem;
        if (currentItem != null)
        {
            var currentCraft = _workshopCache.Crafts.Single(x => x.WorkshopItemId == currentItem.WorkshopItemId);
            ImGui.Text($"Currently Crafting:");

            IDalamudTextureWrap? icon = _iconCache.GetIcon(currentCraft.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2);
            }
            icon?.Dispose();

            ImGui.TextUnformatted($"{currentCraft.Name}");
            ImGui.Spacing();

            if (_plugin.CurrentStage == Stage.Stopped)
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Search, "Check Inventory"))
                    _checkInventory = !_checkInventory;

                ImGui.SameLine();
                ImGui.BeginDisabled(!NearFabricationStation || !IsDiscipleOfHand);
                if (currentItem.StartedCrafting)
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Resume"))
                    {
                        State = ButtonState.Resume;
                        _checkInventory = false;
                    }
                }
                else
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Start Crafting"))
                    {
                        State = ButtonState.Start;
                        _checkInventory = false;
                    }
                }

                ImGui.EndDisabled();

                ImGui.SameLine();

                bool keysHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
                ImGui.BeginDisabled(!keysHeld);
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Cancel"))
                {
                    State = ButtonState.Pause;
                    _configuration.CurrentlyCraftedItem = null;

                    Save();
                }

                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !keysHeld)
                    ImGui.SetTooltip(
                        $"Hold CTRL+SHIFT to remove this as craft. You have to manually use the fabrication station to cancel or finish the workshop project before you can continue using the queue.");

                ShowErrorConditions();
            }
            else
            {
                ImGui.BeginDisabled(_plugin.CurrentStage == Stage.RequestStop);
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Pause, "Pause"))
                    State = ButtonState.Pause;

                ImGui.EndDisabled();
            }
        }
        else
        {
            ImGui.Text("Currently Crafting: ---");

            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Search, "Check Inventory"))
                _checkInventory = !_checkInventory;

            ImGui.SameLine();
            ImGui.BeginDisabled(!NearFabricationStation || _configuration.ItemQueue.Sum(x => x.Quantity) == 0 ||
                                _plugin.CurrentStage != Stage.Stopped || !IsDiscipleOfHand);
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Start Crafting"))
            {
                State = ButtonState.Start;
                _checkInventory = false;
            }

            ImGui.EndDisabled();

            ShowErrorConditions();
        }

        if (_checkInventory)
        {
            ImGui.Separator();
            CheckMaterial();
        }

        ImGui.Separator();
        ImGui.Text("Queue:");
        ImGui.BeginDisabled(_plugin.CurrentStage != Stage.Stopped);
        Configuration.QueuedItem? itemToRemove = null;
        for (int i = 0; i < _configuration.ItemQueue.Count; ++i)
        {
            using var _ = ImRaii.PushId($"ItemQueue{i}");
            var item = _configuration.ItemQueue[i];
            var craft = _workshopCache.Crafts.Single(x => x.WorkshopItemId == item.WorkshopItemId);

            IDalamudTextureWrap? icon = _iconCache.GetIcon(craft.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            }
            icon?.Dispose();

            ImGui.SetNextItemWidth(Math.Max(100 * ImGui.GetIO().FontGlobalScale, 4 * (ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X)));
            int quantity = item.Quantity;
            if (ImGui.InputInt(craft.Name, ref quantity))
            {
                item.Quantity = Math.Max(0, quantity);
                Save();
            }

            ImGui.OpenPopupOnItemClick($"###Context{i}");
            using var popup = ImRaii.ContextPopup($"###Context{i}");
            if (popup)
            {
                if (ImGui.MenuItem($"Remove {craft.Name}"))
                    itemToRemove = item;
            }
        }

        if (itemToRemove != null)
        {
            _configuration.ItemQueue.Remove(itemToRemove);
            Save();
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##CraftSelection", "Add Craft...", ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            ImGui.InputTextWithHint("", "Filter...", ref _searchString, 256);

            foreach (var craft in _workshopCache.Crafts
                         .Where(x => x.Name.Contains(_searchString, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.WorkshopItemId))
            {
                IDalamudTextureWrap? icon = _iconCache.GetIcon(craft.IconId);
                Vector2 pos = ImGui.GetCursorPos();
                Vector2 iconSize = new Vector2(ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y);
                if (icon != null)
                {
                    ImGui.SetCursorPos(pos + new Vector2(iconSize.X + ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().ItemSpacing.Y / 2));
                }

                if (ImGui.Selectable($"{craft.Name}##SelectCraft{craft.WorkshopItemId}", false, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _configuration.ItemQueue.Add(new Configuration.QueuedItem
                    {
                        WorkshopItemId = craft.WorkshopItemId,
                        Quantity = 1,
                    });
                    Save();
                }

                if (icon != null)
                {
                    ImGui.SameLine(0, 0);
                    ImGui.SetCursorPos(pos);
                    ImGui.Image(icon.Handle, iconSize);
                }
                icon?.Dispose();
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.Text($"Debug (Stage): {_plugin.CurrentStage}");
    }

    private void DrawPresetsMenu()
    {
        if (ImGui.BeginMenu("Presets"))
        {
            if (_configuration.Presets.Count == 0)
            {
                ImGui.BeginDisabled();
                ImGui.MenuItem("Import Queue from Preset");
                ImGui.EndDisabled();
            }
            else if (ImGui.BeginMenu("Import Queue from Preset"))
            {
                if (_configuration.Presets.Count == 0)
                    ImGui.MenuItem("You have no presets.");

                foreach (var preset in _configuration.Presets)
                {
                    ImGui.PushID($"Preset{preset.Id}");
                    if (ImGui.MenuItem(preset.Name))
                    {
                        foreach (var item in preset.ItemQueue)
                        {
                            var queuedItem =
                                _configuration.ItemQueue.FirstOrDefault(x => x.WorkshopItemId == item.WorkshopItemId);
                            if (queuedItem != null)
                                queuedItem.Quantity += item.Quantity;
                            else
                            {
                                _configuration.ItemQueue.Add(new Configuration.QueuedItem
                                {
                                    WorkshopItemId = item.WorkshopItemId,
                                    Quantity = item.Quantity,
                                });
                            }
                        }

                        Save();
                        _chatGui.Print($"Imported {preset.ItemQueue.Count} items from preset.");
                    }

                    ImGui.PopID();
                }

                ImGui.EndMenu();
            }

            if (_configuration.ItemQueue.Count == 0)
            {
                ImGui.BeginDisabled();
                ImGui.MenuItem("Export Queue to Preset");
                ImGui.EndDisabled();
            }
            else if (ImGui.BeginMenu("Export Queue to Preset"))
            {
                ImGui.InputTextWithHint("", "Preset Name...", ref _newPresetName, 64);

                ImGui.BeginDisabled(_configuration.Presets.Any(x =>
                    x.Name.Equals(_newPresetName, StringComparison.OrdinalIgnoreCase)));
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save"))
                {
                    _configuration.Presets.Add(new Configuration.Preset
                    {
                        Id = Guid.NewGuid(),
                        Name = _newPresetName,
                        ItemQueue = _configuration.ItemQueue.Select(x => new Configuration.QueuedItem
                        {
                            WorkshopItemId = x.WorkshopItemId,
                            Quantity = x.Quantity
                        }).ToList()
                    });

                    Save();
                    _chatGui.Print($"Saved queue as preset '{_newPresetName}'.");

                    _newPresetName = string.Empty;
                }

                ImGui.EndDisabled();

                ImGui.EndMenu();
            }

            if (_configuration.Presets.Count == 0)
            {
                ImGui.BeginDisabled();
                ImGui.MenuItem("Delete Preset");
                ImGui.EndDisabled();
            }
            else if (ImGui.BeginMenu("Delete Preset"))
            {
                if (_configuration.Presets.Count == 0)
                    ImGui.MenuItem("You have no presets.");

                Guid? presetToRemove = null;
                foreach (var preset in _configuration.Presets)
                {
                    ImGui.PushID($"Preset{preset.Id}");
                    if (ImGui.MenuItem(preset.Name))
                    {
                        presetToRemove = preset.Id;
                    }

                    ImGui.PopID();
                }

                if (presetToRemove != null)
                {
                    var preset = _configuration.Presets.First(x => x.Id == presetToRemove);
                    _configuration.Presets.Remove(preset);

                    Save();
                    _chatGui.Print($"Deleted preset '{preset.Name}'.");
                }

                ImGui.EndMenu();
            }

            ImGui.EndMenu();
        }
    }

    private void DrawClipboardMenu()
    {
        if (ImGui.BeginMenu("Clipboard"))
        {
            List<Configuration.QueuedItem> fromClipboardItems = new();
            try
            {
                string? clipboardText = GetClipboardText();
                if (!string.IsNullOrWhiteSpace(clipboardText))
                {
                    foreach (var clipboardLine in clipboardText.ReplaceLineEndings().Split(Environment.NewLine))
                    {
                        var match = CountAndName.Match(clipboardLine);
                        if (!match.Success)
                            continue;

                        var craft = _workshopCache.Crafts.FirstOrDefault(x =>
                            x.Name.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
                        if (craft != null && int.TryParse(match.Groups[1].Value, out int quantity))
                        {
                            fromClipboardItems.Add(new Configuration.QueuedItem
                            {
                                WorkshopItemId = craft.WorkshopItemId,
                                Quantity = quantity,
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                //_pluginLog.Warning(e, "Unable to extract clipboard text");
            }

            ImGui.BeginDisabled(fromClipboardItems.Count == 0);
            if (ImGui.MenuItem("Import Queue from Clipboard"))
            {
                _pluginLog.Information($"Importing {fromClipboardItems.Count} items...");
                int count = 0;
                foreach (var item in fromClipboardItems)
                {
                    var queuedItem =
                        _configuration.ItemQueue.FirstOrDefault(x => x.WorkshopItemId == item.WorkshopItemId);
                    if (queuedItem != null)
                        queuedItem.Quantity += item.Quantity;
                    else
                    {
                        _configuration.ItemQueue.Add(new Configuration.QueuedItem
                        {
                            WorkshopItemId = item.WorkshopItemId,
                            Quantity = item.Quantity,
                        });
                    }

                    ++count;
                }

                Save();
                _chatGui.Print($"Imported {count} items from clipboard.");
            }

            ImGui.EndDisabled();

            ImGui.BeginDisabled(_configuration.ItemQueue.Count == 0);
            if (ImGui.MenuItem("Export Queue to Clipboard"))
            {
                var toClipboardItems = _configuration.ItemQueue.Select(x =>
                        new
                        {
                            _workshopCache.Crafts.Single(y => x.WorkshopItemId == y.WorkshopItemId).Name,
                            x.Quantity
                        })
                    .Select(x => $"{x.Quantity}x {x.Name}");
                ImGui.SetClipboardText(string.Join(Environment.NewLine, toClipboardItems));

                _chatGui.Print("Copied queue content to clipboard.");
            }

            if (ImGui.MenuItem("Export Material List to Clipboard"))
            {
                var toClipboardItems = _recipeTree.ResolveRecipes(GetMaterialList()).Where(x => x.Type == Ingredient.EType.Craftable);
                ImGui.SetClipboardText(string.Join(Environment.NewLine, toClipboardItems.Select(x => $"{x.TotalQuantity}x {x.Name}")));

                _chatGui.Print("Copied material list to clipboard.");
            }

            if (ImGui.MenuItem("Export Gathered/Venture materials to Clipboard"))
            {
                var toClipboardItems = _recipeTree.ResolveRecipes(GetMaterialList()).Where(x => x.Type == Ingredient.EType.Gatherable);
                ImGui.SetClipboardText(string.Join(Environment.NewLine, toClipboardItems.Select(x => $"{x.TotalQuantity}x {x.Name}")));

                _chatGui.Print("Copied material list to clipboard.");
            }

            ImGui.EndDisabled();

            ImGui.EndMenu();
        }
    }

    /// <summary>
    /// The default implementation for <see cref="ImGui.GetClipboardText"/> throws an NullReferenceException if the clipboard is empty, maybe also if it doesn't contain text.
    /// </summary>
    private unsafe string? GetClipboardText()
    {
        byte* ptr = ImGuiNative.GetClipboardText();
        if (ptr == null)
            return null;

        int byteCount = 0;
        while (ptr[byteCount] != 0)
            ++byteCount;
        return Encoding.UTF8.GetString(ptr, byteCount);
    }

    private void Save()
    {
        _pluginInterface.SavePluginConfig(_configuration);
    }

    public void Toggle(EOpenReason reason)
    {
        if (!IsOpen)
        {
            IsOpen = true;
            OpenReason = reason;
        }
        else
            IsOpen = false;
    }

    public override void OnClose()
    {
        OpenReason = EOpenReason.None;
    }

    private unsafe void CheckMaterial()
    {
        ImGui.Text("Items needed for all crafts in queue:");
        var items = GetMaterialList();

        ImGui.Indent(20);
        InventoryManager* inventoryManager = InventoryManager.Instance();
        foreach (var item in items)
        {
            int inInventory = inventoryManager->GetInventoryItemCount(item.ItemId, true, false, false) +
                              inventoryManager->GetInventoryItemCount(item.ItemId, false, false, false);

            IDalamudTextureWrap? icon = _iconCache.GetIcon(item.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2);

                icon.Dispose();
            }

            ImGui.TextColored(inInventory >= item.TotalQuantity ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
                $"{item.Name} ({inInventory} / {item.TotalQuantity})");
        }

        ImGui.Unindent(20);
    }

    private List<Ingredient> GetMaterialList()
    {
        List<uint> workshopItemIds = _configuration.ItemQueue
            .SelectMany(x => Enumerable.Range(0, x.Quantity).Select(_ => x.WorkshopItemId))
            .ToList();
        Dictionary<uint, int> completedForCurrentCraft = new();
        var currentItem = _configuration.CurrentlyCraftedItem;
        if (currentItem != null)
        {
            workshopItemIds.Add(currentItem.WorkshopItemId);

            var craft = _workshopCache.Crafts.Single(x =>
                x.WorkshopItemId == currentItem.WorkshopItemId);
            for (int i = 0; i < currentItem.PhasesComplete; ++i)
            {
                foreach (var item in craft.Phases[i].Items)
                    AddMaterial(completedForCurrentCraft, item.ItemId, item.TotalQuantity);
            }

            if (currentItem.PhasesComplete < craft.Phases.Count)
            {
                foreach (var item in currentItem.ContributedItemsInCurrentPhase)
                    AddMaterial(completedForCurrentCraft, item.ItemId, (int)item.QuantityComplete);
            }
        }

        return workshopItemIds.Select(x => _workshopCache.Crafts.Single(y => y.WorkshopItemId == x))
            .SelectMany(x => x.Phases)
            .SelectMany(x => x.Items)
            .GroupBy(x => new { x.Name, x.ItemId, x.IconId })
            .OrderBy(x => x.Key.Name)
            .Select(x => new Ingredient
            {
                ItemId = x.Key.ItemId,
                IconId = x.Key.IconId,
                Name = x.Key.Name,
                TotalQuantity = completedForCurrentCraft.TryGetValue(x.Key.ItemId, out var completed)
                    ? x.Sum(y => y.TotalQuantity) - completed
                    : x.Sum(y => y.TotalQuantity),
                Type = Ingredient.EType.Craftable,
            })
            .ToList();
    }

    private static void AddMaterial(Dictionary<uint, int> completedForCurrentCraft, uint itemId, int quantity)
    {
        if (completedForCurrentCraft.TryGetValue(itemId, out var existingQuantity))
            completedForCurrentCraft[itemId] = quantity + existingQuantity;
        else
            completedForCurrentCraft[itemId] = quantity;
    }

    private void ShowErrorConditions()
    {
        if (!_plugin.WorkshopTerritories.Contains((ushort)_clientState.TerritoryType))
            ImGui.TextColored(ImGuiColors.DalamudRed, "You are not in the Company Workshop.");
        else if (!NearFabricationStation)
            ImGui.TextColored(ImGuiColors.DalamudRed, "You are not near a Fabrication Station.");

        if (!IsDiscipleOfHand)
            ImGui.TextColored(ImGuiColors.DalamudRed, "You need to be a Disciple of the Hand to start crafting.");
    }

    public void SaveWindowConfig() => Save();

    public enum ButtonState
    {
        None,
        Start,
        Resume,
        Pause,
        Stop,
    }

    public enum EOpenReason
    {
        None,
        Command,
        NearFabricationStation,
        PluginInstaller,
    }
}
