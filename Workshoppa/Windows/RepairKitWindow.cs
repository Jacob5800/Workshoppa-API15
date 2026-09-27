using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;
using LLib.Shop.Model;
using Workshoppa.External;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Workshoppa.Windows;

internal sealed class RepairKitWindow : ShopWindow
{
    private const int DarkMatterCluster6ItemId = 10386;

    private readonly IPluginLog _pluginLog;
    private readonly Configuration _configuration;

    public RepairKitWindow(
        IPluginLog pluginLog,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        Configuration configuration,
        ExternalPluginHandler externalPluginHandler)
        : base("Repair Kits###WorkshoppaRepairKitWindow", "Shop", pluginLog, gameGui, addonLifecycle, externalPluginHandler)
    {
        _pluginLog = pluginLog;
        _configuration = configuration;
    }

    public override bool IsEnabled => _configuration.EnableRepairKitCalculator;

    public override unsafe void UpdateShopStock(AtkUnitBase* addon)
    {
        if (GetDarkMatterClusterCount() == 0)
        {
            Shop.ItemForSale = null;
            return;
        }

        if (addon->AtkValuesCount != 625)
        {
            _pluginLog.Error($"Unexpected amount of atkvalues for Shop addon ({addon->AtkValuesCount})");
            Shop.ItemForSale = null;
            return;
        }

        var atkValues = addon->AtkValues;

        // Check if on 'Current Stock' tab?
        if (atkValues[0].UInt != 0)
        {
            Shop.ItemForSale = null;
            return;
        }

        uint itemCount = atkValues[2].UInt;
        if (itemCount == 0)
        {
            Shop.ItemForSale = null;
            return;
        }

        Shop.ItemForSale = Enumerable.Range(0, (int)itemCount)
            .Select(i => new ItemForSale
            {
                Position = i,
                ItemName = atkValues[14 + i].ReadAtkString(),
                Price = atkValues[75 + i].UInt,
                OwnedItems = atkValues[136 + i].UInt,
                ItemId = atkValues[441 + i].UInt,
            })
            .FirstOrDefault(x => x.ItemId == DarkMatterCluster6ItemId);
    }

    private int GetDarkMatterClusterCount() => Shop.GetItemCount(10335);

    public override int GetCurrencyCount() => Shop.GetItemCount(1);

    public override void DrawContent()
    {
        int darkMatterClusters = GetDarkMatterClusterCount();
        if (Shop.ItemForSale == null || darkMatterClusters == 0)
        {
            IsOpen = false;
            return;
        }

        ImGui.Text("Inventory");
        ImGui.Indent();
        ImGui.Text($"Dark Matter Clusters: {darkMatterClusters:N0}");
        ImGui.Text($"Grade 6 Dark Matter: {Shop.ItemForSale.OwnedItems:N0}");
        ImGui.Unindent();

        int missingItems = Math.Max(0, darkMatterClusters * 5 - (int)Shop.ItemForSale.OwnedItems);
        ImGui.TextColored(missingItems == 0 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            $"Missing Grade 6 Dark Matter: {missingItems:N0}");

        if (Shop.PurchaseState != null)
        {
            Shop.HandleNextPurchaseStep();
            if (Shop.PurchaseState != null)
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Cancel Auto-Buy"))
                    Shop.CancelAutoPurchase();
            }
        }
        else
        {
            int toPurchase = Math.Min(Shop.GetMaxItemsToPurchase(), missingItems);
            if (toPurchase > 0)
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.DollarSign,
                        $"Auto-Buy missing Dark Matter for {Shop.ItemForSale.Price * toPurchase:N0}{SeIconChar.Gil.ToIconString()}"))
                {
                    Shop.StartAutoPurchase(toPurchase);
                    Shop.HandleNextPurchaseStep();
                }
            }
        }
    }

    public override unsafe void TriggerPurchase(AtkUnitBase* addonShop, int buyNow)
    {
        var buyItem = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.Int, Int = Shop.ItemForSale!.Position },
            new() { Type = AtkValueType.Int, Int = buyNow },
            new() { Type = 0, Int = 0 }
        };
        addonShop->FireCallback(4, buyItem);
    }
}
