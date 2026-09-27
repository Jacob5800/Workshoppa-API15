using System;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;
using Workshoppa.GameData;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private uint? _contributingItemId;
    private bool _workshopTransitionCutsceneSkipPending;
    private bool _workshopTransitionCutsceneSkipRequested;
    private DateTime _workshopTransitionCutsceneSkipExpiresAt;
    private DateTime _nextWorkshopTransitionCutsceneSkipAttempt;

    private void ArmWorkshopTransitionCutsceneSkip()
    {
        _workshopTransitionCutsceneSkipPending = true;
        _workshopTransitionCutsceneSkipRequested = false;
        _workshopTransitionCutsceneSkipExpiresAt = DateTime.Now.AddSeconds(15);
        _nextWorkshopTransitionCutsceneSkipAttempt = DateTime.MinValue;
    }

    private void ClearWorkshopTransitionCutsceneSkip()
    {
        _workshopTransitionCutsceneSkipPending = false;
        _workshopTransitionCutsceneSkipRequested = false;
        _workshopTransitionCutsceneSkipExpiresAt = DateTime.MinValue;
        _nextWorkshopTransitionCutsceneSkipAttempt = DateTime.MinValue;
    }

    private unsafe void TrySkipWorkshopTransitionCutscene()
    {
        if (!_workshopTransitionCutsceneSkipPending)
            return;

        var now = DateTime.Now;
        if (now >= _workshopTransitionCutsceneSkipExpiresAt)
        {
            _pluginLog.Warning("Workshop transition cutscene skip request expired before the skip prompt appeared");
            ClearWorkshopTransitionCutsceneSkip();
            return;
        }

        // WatchingCutscene (58) is not the flag used by all of the game's current cutscene paths.
        // The UI builder considers OccupiedInCutSceneEvent and WatchingCutscene78 the active
        // cutscene conditions, so accept those as well for the workshop transition scenes.
        bool isWatchingCutscene = _condition[ConditionFlag.WatchingCutscene] ||
                                  _condition[ConditionFlag.WatchingCutscene78] ||
                                  _condition[ConditionFlag.OccupiedInCutSceneEvent];
        if (!isWatchingCutscene ||
            _workshopTransitionCutsceneSkipRequested ||
            now < _nextWorkshopTransitionCutsceneSkipAttempt)
            return;

        _nextWorkshopTransitionCutsceneSkipAttempt = now.AddMilliseconds(250);

        AgentModule* agentModule = AgentModule.Instance();
        if (agentModule == null)
            return;

        AgentCutscene* cutsceneAgent = (AgentCutscene*)agentModule->GetAgentByInternalId(AgentId.Cutscene);
        if (cutsceneAgent == null || cutsceneAgent->SkipCallback == null || cutsceneAgent->SkipDialogAddonId != 0)
            return;

        if (cutsceneAgent->OpenSkipDialog(cutsceneAgent->SkipCallback))
        {
            _pluginLog.Information("Opening skip prompt for workshop transition cutscene");
            _workshopTransitionCutsceneSkipRequested = true;
        }
    }

    private unsafe bool IsWorkshopTransitionCutsceneSkipDialog(AtkUnitBase* addon)
    {
        if (!_workshopTransitionCutsceneSkipPending || addon == null)
            return false;

        AgentModule* agentModule = AgentModule.Instance();
        if (agentModule == null)
            return false;

        AgentCutscene* cutsceneAgent = (AgentCutscene*)agentModule->GetAgentByInternalId(AgentId.Cutscene);
        if (cutsceneAgent == null || cutsceneAgent->SkipDialogAddonId == 0)
            return false;

        return LAddon.GetAddonById(cutsceneAgent->SkipDialogAddonId) == addon;
    }

    /// <summary>
    /// Check if delivery window is open when we clicked resume.
    /// </summary>
    private unsafe bool CheckContinueWithDelivery()
    {
        if (_configuration.CurrentlyCraftedItem != null)
        {
            AtkUnitBase* addonMaterialDelivery = GetMaterialDeliveryAddon();
            if (addonMaterialDelivery == null)
                return false;

            _pluginLog.Warning("Material delivery window is open, although unexpected... checking current craft");
            CraftState? craftState = ReadCraftState(addonMaterialDelivery);
            if (craftState == null || craftState.ResultItem == 0)
            {
                _pluginLog.Error("Unable to read craft state");
                _continueAt = DateTime.Now.AddSeconds(1);
                return false;
            }

            var craft = _workshopCache.Crafts.SingleOrDefault(x => x.ResultItem == craftState.ResultItem);
            if (craft == null || craft.WorkshopItemId != _configuration.CurrentlyCraftedItem.WorkshopItemId)
            {
                _pluginLog.Error("Unable to match currently crafted item with game state");
                _continueAt = DateTime.Now.AddSeconds(1);
                return false;
            }

            _pluginLog.Information("Delivering materials for current active craft, switching to delivery");
            return true;
        }

        return false;
    }

    private void SelectCraftBranch()
    {
        if (SelectSelectString("contrib", 0, s => s.StartsWith("Contribute materials.", StringComparison.Ordinal)))
        {
            CurrentStage = Stage.ContributeMaterials;
            _continueAt = DateTime.Now.AddSeconds(1);
        }
        else if (SelectSelectString("advance", 0, s => s.StartsWith("Advance to the next phase of production.", StringComparison.Ordinal)))
        {
            _pluginLog.Information("Phase is complete");
            ArmWorkshopTransitionCutsceneSkip();

            _configuration.CurrentlyCraftedItem!.PhasesComplete++;
            _configuration.CurrentlyCraftedItem!.ContributedItemsInCurrentPhase = new();
            _pluginInterface.SavePluginConfig(_configuration);

            CurrentStage = Stage.TargetFabricationStation;
            _continueAt = DateTime.Now.AddSeconds(3);
        }
        else if (SelectSelectString("complete", 0, s => s.StartsWith("Complete the construction of", StringComparison.Ordinal)))
        {
            _pluginLog.Information("Item is almost complete, confirming last cutscene");
            ArmWorkshopTransitionCutsceneSkip();
            CurrentStage = Stage.TargetFabricationStation;
            _continueAt = DateTime.Now.AddSeconds(3);
        }
        else if (SelectSelectString("collect", 0, s => s == "Collect finished product."))
        {
            _pluginLog.Information("Item is complete");
            ClearWorkshopTransitionCutsceneSkip();
            CurrentStage = Stage.ConfirmCollectProduct;
            _continueAt = DateTime.Now.AddSeconds(0.25);
        }
    }

    private unsafe void ContributeMaterials()
    {
        AtkUnitBase* addonMaterialDelivery = GetMaterialDeliveryAddon();
        if (addonMaterialDelivery == null)
            return;

        CraftState? craftState = ReadCraftState(addonMaterialDelivery);
        if (craftState == null || craftState.ResultItem == 0)
        {
            _pluginLog.Warning("Could not parse craft state");
            _continueAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (_configuration.CurrentlyCraftedItem!.UpdateFromCraftState(craftState))
        {
            _pluginLog.Information("Saving updated current craft information");
            _pluginInterface.SavePluginConfig(_configuration);
        }

        for (int i = 0; i < craftState.Items.Count; ++i)
        {
            var item = craftState.Items[i];
            if (item.Finished)
                continue;

            if (!HasItemInSingleSlot(item.ItemId, item.ItemCountPerStep))
            {
                _pluginLog.Error(
                    $"Can't contribute item {item.ItemId} to craft, couldn't find {item.ItemCountPerStep}x in a single inventory slot");

                InventoryManager* inventoryManager = InventoryManager.Instance();
                int itemCount = 0;
                if (inventoryManager != null)
                {
                    itemCount = inventoryManager->GetInventoryItemCount(item.ItemId, true, false, false) +
                                inventoryManager->GetInventoryItemCount(item.ItemId, false, false, false);
                }

                if (itemCount < item.ItemCountPerStep)
                    _chatGui.PrintError(
                        $"[Workshoppa] You don't have the needed {item.ItemCountPerStep}x {item.ItemName} to continue.");
                else
                    _chatGui.PrintError(
                        $"[Workshoppa] You don't have {item.ItemCountPerStep}x {item.ItemName} in a single stack, you need to merge the items in your inventory manually to continue.");

                CurrentStage = Stage.RequestStop;
                break;
            }

            _externalPluginHandler.SaveTextAdvance();

            _pluginLog.Information($"Contributing {item.ItemCountPerStep}x {item.ItemName}");
            _contributingItemId = item.ItemId;
            var contributeMaterial = stackalloc AtkValue[]
            {
                new() { Type = AtkValueType.Int, Int = 0 },
                new() { Type = AtkValueType.UInt, Int = i },
                new() { Type = AtkValueType.UInt, UInt = item.ItemCountPerStep },
                new() { Type = 0, Int = 0 }
            };
            addonMaterialDelivery->FireCallback(4, contributeMaterial);
            _fallbackAt = DateTime.Now.AddSeconds(0.2);
            CurrentStage = Stage.OpenRequestItemWindow;
            break;
        }
    }

    private unsafe void RequestPostSetup(AddonEvent type, AddonArgs addon)
    {
        var addonRequest = (AddonRequest*)addon.Addon.Address;
        _pluginLog.Verbose($"{nameof(RequestPostSetup)}: {CurrentStage}, {addonRequest->EntryCount}");
        if (CurrentStage != Stage.OpenRequestItemWindow)
            return;

        if (addonRequest->EntryCount != 1)
            return;

        _fallbackAt = DateTime.MaxValue;
        CurrentStage = Stage.OpenRequestItemSelect;
        var contributeMaterial = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 2 },
            new() { Type = AtkValueType.UInt, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = 44 },
            new() { Type = AtkValueType.UInt, UInt = 0 }
        };
        addonRequest->AtkUnitBase.FireCallback(4, contributeMaterial);
    }

    private unsafe void ContextIconMenuPostReceiveEvent(AddonEvent type, AddonArgs addon)
    {
        if (CurrentStage != Stage.OpenRequestItemSelect)
            return;

        CurrentStage = Stage.ConfirmRequestItemWindow;
        var selectSlot = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.Int, Int = 0 /* slot */ },
            new() { Type = AtkValueType.UInt, UInt = 20802 /* probably the item's icon */ },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = 0, Int = 0 },
        };
        ((AddonContextIconMenu*)addon.Addon.Address)->AtkUnitBase.FireCallback(5, selectSlot);
    }

    private unsafe void RequestPostRefresh(AddonEvent type, AddonArgs addon)
    {
        _pluginLog.Verbose($"{nameof(RequestPostRefresh)}: {CurrentStage}");
        if (CurrentStage != Stage.ConfirmRequestItemWindow)
            return;

        var addonRequest = (AddonRequest*)addon.Addon.Address;
        if (addonRequest->EntryCount != 1)
            return;

        CurrentStage = Stage.ConfirmMaterialDelivery;
        var closeWindow = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 }
        };
        addonRequest->AtkUnitBase.FireCallback(4, closeWindow);
        addonRequest->AtkUnitBase.Close(false);
        _externalPluginHandler.RestoreTextAdvance();
    }

    private unsafe void ConfirmMaterialDeliveryFollowUp()
    {
        AtkUnitBase* addonMaterialDelivery = GetMaterialDeliveryAddon();
        if (addonMaterialDelivery == null)
            return;

        CraftState? craftState = ReadCraftState(addonMaterialDelivery);
        if (craftState == null || craftState.ResultItem == 0)
        {
            _pluginLog.Warning("Could not parse craft state");
            _continueAt = DateTime.Now.AddSeconds(1);
            return;
        }

        var item = craftState.Items.Single(x => x.ItemId == _contributingItemId);
        item.StepsComplete++;
        if (craftState.IsPhaseComplete())
        {
            CurrentStage = Stage.TargetFabricationStation;
            _continueAt = DateTime.Now.AddSeconds(0.5);
        }
        else
        {
            _configuration.CurrentlyCraftedItem!.ContributedItemsInCurrentPhase
                .Single(x => x.ItemId == item.ItemId)
                .QuantityComplete = item.QuantityComplete;
            _pluginInterface.SavePluginConfig(_configuration);

            CurrentStage = Stage.ContributeMaterials;
            _continueAt = DateTime.Now.AddSeconds(1);
        }
    }
}
