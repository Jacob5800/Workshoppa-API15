using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Workshoppa;

partial class WorkshopPlugin
{
    private unsafe void SelectYesNoPostSetup(AddonEvent type, AddonArgs args)
    {
        _pluginLog.Verbose("SelectYesNo post-setup");

        AddonSelectYesno* addonSelectYesNo = (AddonSelectYesno*)args.Addon.Address;
        string text = addonSelectYesNo->PromptText->NodeText.ExtractText()
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal);
        _pluginLog.Verbose($"YesNo prompt: '{text}'");

        if (IsWorkshopTransitionCutsceneSkipDialog(&addonSelectYesNo->AtkUnitBase))
        {
            _pluginLog.Information("Confirming skip for workshop transition cutscene");
            addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            ClearWorkshopTransitionCutsceneSkip();
            _continueAt = DateTime.Now.AddSeconds(0.5);
        }
        else if (_repairKitWindow.IsOpen)
        {
            _pluginLog.Verbose($"Checking for Repair Kit YesNo ({_repairKitWindow.AutoBuyEnabled}, {_repairKitWindow.IsAwaitingYesNo})");
            if (_repairKitWindow.AutoBuyEnabled && _repairKitWindow.IsAwaitingYesNo && _gameStrings.PurchaseItemForGil.IsMatch(text))
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                _repairKitWindow.IsAwaitingYesNo = false;
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else
            {
                _pluginLog.Verbose("Not a purchase confirmation match");
            }
        }
        else if (_ceruleumTankWindow.IsOpen)
        {
            _pluginLog.Verbose($"Checking for Ceruleum Tank YesNo ({_ceruleumTankWindow.AutoBuyEnabled}, {_ceruleumTankWindow.IsAwaitingYesNo})");
            if (_ceruleumTankWindow.AutoBuyEnabled && _ceruleumTankWindow.IsAwaitingYesNo && _gameStrings.PurchaseItemForCompanyCredits.IsMatch(text))
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                _ceruleumTankWindow.IsAwaitingYesNo = false;
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else
            {
                _pluginLog.Verbose("Not a purchase confirmation match");
            }
        }
        else if (CurrentStage != Stage.Stopped)
        {
            if (CurrentStage == Stage.ConfirmMaterialDelivery && _gameStrings.TurnInHighQualityItem == text)
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);
            }
            else if (CurrentStage == Stage.ConfirmMaterialDelivery && _gameStrings.ContributeItems.IsMatch(text))
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);

                ConfirmMaterialDeliveryFollowUp();
            }
            else if (CurrentStage == Stage.ConfirmCollectProduct && _gameStrings.RetrieveFinishedItem.IsMatch(text))
            {
                _pluginLog.Information($"Selecting 'yes' ({text})");
                addonSelectYesNo->AtkUnitBase.FireCallbackInt(0);

                ConfirmCollectProductFollowUp();
            }
        }
    }

    private void ConfirmCollectProductFollowUp()
    {
        _configuration.CurrentlyCraftedItem = null;
        _pluginInterface.SavePluginConfig(_configuration);

        CurrentStage = Stage.TakeItemFromQueue;
        _continueAt = DateTime.Now.AddSeconds(0.5);
    }
}
