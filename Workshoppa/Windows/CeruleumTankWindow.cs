using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;
using LLib.Shop.Model;
using Workshoppa.External;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Workshoppa.Windows;

internal sealed class CeruleumTankWindow : ShopWindow
{
    private const int CeruleumTankItemId = 10155;

    private readonly IPluginLog _pluginLog;
    private readonly Configuration _configuration;
    private readonly IChatGui _chatGui;

    private int _companyCredits;
    private int _buyStackCount;
    private bool _buyPartialStacks = true;

    public CeruleumTankWindow(
        IPluginLog pluginLog,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        Configuration configuration,
        ExternalPluginHandler externalPluginHandler,
        IChatGui chatGui)
        : base("Ceruleum Tanks###WorkshoppaCeruleumTankWindow", "FreeCompanyCreditShop", pluginLog, gameGui,
            addonLifecycle, externalPluginHandler)
    {
        _pluginLog = pluginLog;
        _chatGui = chatGui;
        _configuration = configuration;
    }

    public override bool IsEnabled => _configuration.EnableCeruleumTankCalculator;

    public override unsafe void UpdateShopStock(AtkUnitBase* addon)
    {
        if (addon->AtkValuesCount != 170)
        {
            _pluginLog.Error(
                $"Unexpected amount of atkvalues for FreeCompanyCreditShop addon ({addon->AtkValuesCount})");
            _companyCredits = 0;
            Shop.ItemForSale = null;
            return;
        }

        var atkValues = addon->AtkValues;
        _companyCredits = (int)atkValues[3].UInt;

        uint itemCount = atkValues[9].UInt;
        if (itemCount == 0)
        {
            Shop.ItemForSale = null;
            return;
        }

        Shop.ItemForSale = Enumerable.Range(0, (int)itemCount)
            .Select(i => new ItemForSale
            {
                Position = i,
                ItemName = atkValues[10 + i].ReadAtkString(),
                Price = atkValues[130 + i].UInt,
                OwnedItems = atkValues[90 + i].UInt,
                ItemId = atkValues[30 + i].UInt,
            })
            .FirstOrDefault(x => x.ItemId == CeruleumTankItemId);
    }

    public override int GetCurrencyCount() => _companyCredits;

    public override void DrawContent()
    {
        if (Shop.ItemForSale == null)
        {
            IsOpen = false;
            return;
        }

        int ceruleumTanks = Shop.GetItemCount(CeruleumTankItemId);
        int freeInventorySlots = Shop.CountFreeInventorySlots();

        ImGui.Text("Inventory");
        ImGui.Indent();
        ImGui.Text($"Ceruleum Tanks: {FormatStackCount(ceruleumTanks)}");
        ImGui.Text($"Free Slots: {freeInventorySlots}");
        ImGui.Unindent();

        ImGui.Separator();

        if (Shop.PurchaseState == null)
        {
            ImGui.SetNextItemWidth(100);
            ImGui.InputInt("Stacks to Buy", ref _buyStackCount);
            _buyStackCount = Math.Min(freeInventorySlots, Math.Max(0, _buyStackCount));

            if (ceruleumTanks % 999 > 0)
                ImGui.Checkbox($"Fill Partial Stacks (+{999 - ceruleumTanks % 999})", ref _buyPartialStacks);
        }

        int missingItems = _buyStackCount * 999;
        if (_buyPartialStacks && ceruleumTanks % 999 > 0)
            missingItems += (999 - ceruleumTanks % 999);

        if (Shop.PurchaseState != null)
        {
            Shop.HandleNextPurchaseStep();
            if (Shop.PurchaseState != null)
            {
                ImGui.Text($"Buying {FormatStackCount(Shop.PurchaseState.ItemsLeftToBuy)}...");
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Cancel Auto-Buy"))
                    Shop.CancelAutoPurchase();
            }
        }
        else
        {
            int toPurchase = Math.Min(Shop.GetMaxItemsToPurchase(), missingItems);
            if (toPurchase > 0)
            {
                ImGui.Spacing();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.DollarSign,
                        $"Auto-Buy {FormatStackCount(toPurchase)} for {Shop.ItemForSale.Price * toPurchase:N0} CC"))
                {
                    Shop.StartAutoPurchase(toPurchase);
                    Shop.HandleNextPurchaseStep();
                }
            }
        }
    }

    private static string FormatStackCount(int ceruleumTanks)
    {
        int fullStacks = ceruleumTanks / 999;
        int partials = ceruleumTanks % 999;
        string stacks = fullStacks == 1 ? "stack" : "stacks";
        if (partials > 0)
            return $"{fullStacks:N0} {stacks} + {partials}";
        return $"{fullStacks:N0} {stacks}";
    }

    public override unsafe void TriggerPurchase(AtkUnitBase* addonShop, int buyNow)
    {
        var buyItem = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = (uint)Shop.ItemForSale!.Position },
            new() { Type = AtkValueType.UInt, UInt = (uint)buyNow },
        };
        addonShop->FireCallback(3, buyItem);
    }

    public bool TryParseBuyRequest(string arguments, out int missingQuantity)
    {
        if (!int.TryParse(arguments, out int stackCount) || stackCount <= 0)
        {
            missingQuantity = 0;
            return false;
        }

        int freeInventorySlots = Shop.CountFreeInventorySlots();
        stackCount = Math.Min(freeInventorySlots, stackCount);
        missingQuantity = Math.Min(Shop.GetMaxItemsToPurchase(), stackCount * 999);
        return true;
    }

    public bool TryParseFillRequest(string arguments, out int missingQuantity)
    {
        if (!int.TryParse(arguments, out int stackCount) || stackCount < 0)
        {
            missingQuantity = 0;
            return false;
        }

        int freeInventorySlots = Shop.CountFreeInventorySlots();
        int partialStacks = Shop.CountInventorySlotsWithCondition(CeruleumTankItemId, q => q < 999);
        int fullStacks = Shop.CountInventorySlotsWithCondition(CeruleumTankItemId, q => q == 999);

        int tanks = Math.Min((fullStacks + partialStacks + freeInventorySlots) * 999,
            Math.Max(stackCount * 999, (fullStacks + partialStacks) * 999));
        _pluginLog.Information("T: " + tanks);
        int owned = Shop.GetItemCount(CeruleumTankItemId);
        if (tanks <= owned)
            missingQuantity = 0;
        else
            missingQuantity = Math.Min(Shop.GetMaxItemsToPurchase(), tanks - owned);

        return true;
    }

    public void StartPurchase(int quantity)
    {
        if (!IsOpen || Shop.ItemForSale == null)
        {
            _chatGui.PrintError("Could not start purchase, shop window is not open.");
            return;
        }

        if (quantity <= 0)
        {
            _chatGui.Print("Not buying ceruleum tanks, you already have enough.");
            return;
        }

        _chatGui.Print($"Starting purchase of {FormatStackCount(quantity)} ceruleum tanks.");
        Shop.StartAutoPurchase(quantity);
        Shop.HandleNextPurchaseStep();
    }
}
