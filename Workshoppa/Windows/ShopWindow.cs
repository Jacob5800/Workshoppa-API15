using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.ImGui;
using LLib.Shop;
using Workshoppa.External;

namespace Workshoppa.Windows;

internal abstract class ShopWindow : LWindow, IShopWindow, IDisposable
{
    private readonly ExternalPluginHandler _externalPluginHandler;

    protected ShopWindow(
        string windowName,
        string addonName,
        IPluginLog pluginLog,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        ExternalPluginHandler externalPluginHandler)
        : base(windowName)
    {
        _externalPluginHandler = externalPluginHandler;
        Shop = new RegularShopBase(this, addonName, pluginLog, gameGui, addonLifecycle);

        Position = new Vector2(100, 100);
        PositionCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoCollapse;
    }

    public void Dispose() => Shop.Dispose();

    public bool AutoBuyEnabled => Shop.AutoBuyEnabled;
    public bool IsAwaitingYesNo
    {
        get { return Shop.IsAwaitingYesNo; }
        set { Shop.IsAwaitingYesNo = value; }
    }

    protected RegularShopBase Shop { get; }
    public abstract bool IsEnabled { get; }
    public abstract int GetCurrencyCount();
    public abstract unsafe void UpdateShopStock(AtkUnitBase* addon);
    public abstract unsafe void TriggerPurchase(AtkUnitBase* addonShop, int buyNow);
    public void SaveExternalPluginState() => _externalPluginHandler.Save();
    public void RestoreExternalPluginState() => _externalPluginHandler.Restore();
}
