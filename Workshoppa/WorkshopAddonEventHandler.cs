using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Workshoppa;

internal sealed unsafe class WorkshopAddonEventHandler
{
    private readonly WorkshopPlugin _plugin;
    private readonly IPluginLog _pluginLog;

    public WorkshopAddonEventHandler(WorkshopPlugin plugin, IPluginLog pluginLog)
    {
        _plugin = plugin;
        _pluginLog = pluginLog;
    }

    public void RequestPostSetup(AddonEvent type, AddonArgs addon)
    {
        var addonRequest = (AddonRequest*)addon.Addon.Address;
        _pluginLog.Verbose($"{nameof(RequestPostSetup)}: {_plugin.CurrentStage}, {addonRequest->EntryCount}");
        if (_plugin.CurrentStage != Stage.OpenRequestItemWindow || addonRequest->EntryCount != 1)
            return;

        _plugin.FallbackAt = DateTime.MaxValue;
        _plugin.CurrentStage = Stage.OpenRequestItemSelect;
        var contributeMaterial = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 2 },
            new() { Type = AtkValueType.UInt, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = 44 },
            new() { Type = AtkValueType.UInt, UInt = 0 }
        };
        addonRequest->AtkUnitBase.FireCallback(4, contributeMaterial);
    }

    public void ContextIconMenuPostReceiveEvent(AddonEvent type, AddonArgs addon)
    {
        if (_plugin.CurrentStage != Stage.OpenRequestItemSelect)
            return;

        _plugin.CurrentStage = Stage.ConfirmRequestItemWindow;
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

    public void RequestPostRefresh(AddonEvent type, AddonArgs addon)
    {
        _pluginLog.Verbose($"{nameof(RequestPostRefresh)}: {_plugin.CurrentStage}");
        if (_plugin.CurrentStage != Stage.ConfirmRequestItemWindow)
            return;

        var addonRequest = (AddonRequest*)addon.Addon.Address;
        if (addonRequest->EntryCount != 1)
            return;

        _plugin.CurrentStage = Stage.ConfirmMaterialDelivery;
        var closeWindow = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 }
        };
        addonRequest->AtkUnitBase.FireCallback(4, closeWindow);
        addonRequest->AtkUnitBase.Close(false);
        _plugin.RestoreTextAdvanceAfterRequest();
    }

    public void SelectYesNoPostSetup(AddonEvent type, AddonArgs args)
    {
        _plugin.PluginLog.Verbose("SelectYesNo post-setup");

        AddonSelectYesno* addonSelectYesNo = (AddonSelectYesno*)args.Addon.Address;
        string text = addonSelectYesNo->PromptText->NodeText.ExtractText()
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal);
        _plugin.PluginLog.Verbose($"YesNo prompt: '{text}'");

        if (_plugin.CutsceneSkip.IsSkipDialog(&addonSelectYesNo->AtkUnitBase))
        {
            _plugin.PluginLog.Information("Confirming skip for workshop transition cutscene");
            addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            _plugin.CutsceneSkip.Clear();
            _plugin.ContinueAt = DateTime.Now.AddSeconds(0.5);
        }
        else if (_plugin.RepairKitWindow.IsOpen)
        {
            _plugin.PluginLog.Verbose(
                $"Checking for Repair Kit YesNo ({_plugin.RepairKitWindow.AutoBuyEnabled}, {_plugin.RepairKitWindow.IsAwaitingYesNo})");
            if (_plugin.RepairKitWindow.AutoBuyEnabled && _plugin.RepairKitWindow.IsAwaitingYesNo &&
                _plugin.GameStrings.PurchaseItemForGil.IsMatch(text))
            {
                _plugin.PluginLog.Information($"Selecting 'yes' ({text})");
                _plugin.RepairKitWindow.IsAwaitingYesNo = false;
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else
            {
                _plugin.PluginLog.Verbose("Not a purchase confirmation match");
            }
        }
        else if (_plugin.CeruleumTankWindow.IsOpen)
        {
            _plugin.PluginLog.Verbose(
                $"Checking for Ceruleum Tank YesNo ({_plugin.CeruleumTankWindow.AutoBuyEnabled}, {_plugin.CeruleumTankWindow.IsAwaitingYesNo})");
            if (_plugin.CeruleumTankWindow.AutoBuyEnabled && _plugin.CeruleumTankWindow.IsAwaitingYesNo &&
                _plugin.GameStrings.PurchaseItemForCompanyCredits.IsMatch(text))
            {
                _plugin.PluginLog.Information($"Selecting 'yes' ({text})");
                _plugin.CeruleumTankWindow.IsAwaitingYesNo = false;
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else
            {
                _plugin.PluginLog.Verbose("Not a purchase confirmation match");
            }
        }
        else if (_plugin.CurrentStage != Stage.Stopped)
        {
            if (_plugin.CurrentStage == Stage.ConfirmMaterialDelivery && _plugin.GameStrings.TurnInHighQualityItem == text)
            {
                _plugin.PluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else if (_plugin.CurrentStage == Stage.ConfirmMaterialDelivery && _plugin.GameStrings.ContributeItems.IsMatch(text))
            {
                _plugin.PluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);

                _plugin.ConfirmMaterialDeliveryFollowUp();
            }
            else if (_plugin.CurrentStage == Stage.ConfirmCollectProduct &&
                     _plugin.GameStrings.RetrieveFinishedItem.IsMatch(text))
            {
                _plugin.PluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);

                _plugin.ConfirmCollectProductFollowUp();
            }
        }
    }
}
