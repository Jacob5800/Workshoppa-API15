using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LLib;
using Workshoppa.External;
using Workshoppa.GameData;
using Workshoppa.Windows;

namespace Workshoppa;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public sealed partial class WorkshopPlugin : IDalamudPlugin
{
    private readonly IReadOnlyList<uint> _fabricationStationIds =
        new uint[] { 2005236, 2005238, 2005240, 2007821, 2011588 }.AsReadOnly();

    internal readonly IReadOnlyList<ushort> WorkshopTerritories = new ushort[] { 423, 424, 425, 653, 984 }.AsReadOnly();
    private readonly WindowSystem _windowSystem = new WindowSystem(nameof(WorkshopPlugin));

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGameGui _gameGui;
    private readonly IFramework _framework;
    private readonly ICondition _condition;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICommandManager _commandManager;
    private readonly IPluginLog _pluginLog;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IChatGui _chatGui;

    private readonly Configuration _configuration;
    private readonly ExternalPluginHandler _externalPluginHandler;
    private readonly WorkshopCache _workshopCache;
    private readonly GameStrings _gameStrings;

    private readonly MainWindow _mainWindow;
    private readonly ConfigWindow _configWindow;
    private readonly RepairKitWindow _repairKitWindow;
    private readonly CeruleumTankWindow _ceruleumTankWindow;
    private readonly RetainerDepositWindow _retainerDepositWindow;

    private Stage _currentStageInternal = Stage.Stopped;
    private DateTime _continueAt = DateTime.MinValue;
    private DateTime _fallbackAt = DateTime.MaxValue;

    public WorkshopPlugin(IDalamudPluginInterface pluginInterface, IGameGui gameGui, IFramework framework,
        ICondition condition, IClientState clientState, IObjectTable objectTable, IDataManager dataManager,
        ICommandManager commandManager, IPluginLog pluginLog, IAddonLifecycle addonLifecycle, IChatGui chatGui,
        ITextureProvider textureProvider)
    {
        ArgumentNullException.ThrowIfNull(dataManager);

        _pluginInterface = pluginInterface;
        _gameGui = gameGui;
        _framework = framework;
        _condition = condition;
        _clientState = clientState;
        _objectTable = objectTable;
        _commandManager = commandManager;
        _pluginLog = pluginLog;
        _addonLifecycle = addonLifecycle;
        _chatGui = chatGui;

        _externalPluginHandler = new ExternalPluginHandler(_pluginInterface, _pluginLog);
        _configuration = (Configuration?)_pluginInterface.GetPluginConfig() ?? new Configuration();
        _workshopCache = new WorkshopCache(dataManager, _pluginLog);
        _gameStrings = new(dataManager, _pluginLog);

        _retainerDepositWindow = new(_pluginInterface, _gameGui, dataManager, _configuration, _pluginLog);
        _mainWindow = new(this, _pluginInterface, _clientState, _objectTable, _configuration, _workshopCache,
            new IconCache(textureProvider), _chatGui, new RecipeTree(dataManager, _pluginLog), _pluginLog,
            _retainerDepositWindow);
        _windowSystem.AddWindow(_mainWindow);
        _configWindow = new(_pluginInterface, _configuration);
        _windowSystem.AddWindow(_configWindow);
        _repairKitWindow = new(_pluginLog, _gameGui, addonLifecycle, _configuration,
            _externalPluginHandler);
        _windowSystem.AddWindow(_repairKitWindow);
        _ceruleumTankWindow = new(_pluginLog, _gameGui, addonLifecycle, _configuration,
            _externalPluginHandler, _chatGui);
        _windowSystem.AddWindow(_ceruleumTankWindow);
        _windowSystem.AddWindow(_retainerDepositWindow);

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        _pluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        _framework.Update += FrameworkUpdate;
        _commandManager.AddHandler("/ws", new CommandInfo(ProcessCommand)
        {
            HelpMessage = "Open UI"
        });
        _commandManager.AddHandler("/workshoppa", new CommandInfo(ProcessCommand)
        {
            ShowInHelp = false,
        });
        _commandManager.AddHandler("/buy-tanks", new CommandInfo(ProcessBuyCommand)
        {
            HelpMessage = "Buy a given number of ceruleum tank stacks.",
        });
        _commandManager.AddHandler("/fill-tanks", new CommandInfo(ProcessFillCommand)
        {
            HelpMessage = "Fill your inventory with a given number of ceruleum tank stacks.",
        });

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", SelectYesNoPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Request", RequestPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Request", RequestPostRefresh);
        _addonLifecycle.RegisterListener(AddonEvent.PostUpdate, "ContextIconMenu", ContextIconMenuPostReceiveEvent);
    }

    internal Stage CurrentStage
    {
        get => _currentStageInternal;
        private set
        {
            if (_currentStageInternal != value)
            {
                _pluginLog.Debug($"Changing stage from {_currentStageInternal} to {value}");
                _currentStageInternal = value;
            }

            if (value is Stage.Stopped or Stage.RequestStop)
                ClearWorkshopTransitionCutsceneSkip();

            if (value != Stage.Stopped)
                _mainWindow.Flags |= ImGuiWindowFlags.NoCollapse;
            else
                _mainWindow.Flags &= ~ImGuiWindowFlags.NoCollapse;
        }
    }

    private void FrameworkUpdate(IFramework framework)
    {
        _retainerDepositWindow.UpdateRetainerWindowState();
        _retainerDepositWindow.UpdateTransfer();

        TrySkipWorkshopTransitionCutscene();

        if (!_clientState.IsLoggedIn ||
            !WorkshopTerritories.Contains((ushort)_clientState.TerritoryType) ||
            _condition[ConditionFlag.BoundByDuty] ||
            _condition[ConditionFlag.BetweenAreas] ||
            _condition[ConditionFlag.BetweenAreas51] ||
            GetDistanceToEventObject(_fabricationStationIds, out var fabricationStation) >= 3f)
        {
            _mainWindow.NearFabricationStation = false;

            if (_mainWindow.IsOpen &&
                _mainWindow.OpenReason == MainWindow.EOpenReason.NearFabricationStation &&
                _configuration.CurrentlyCraftedItem == null &&
                _configuration.ItemQueue.Count == 0)
            {
                _mainWindow.IsOpen = false;
            }
        }
        else if (DateTime.Now >= _continueAt)
        {
            _mainWindow.NearFabricationStation = true;

            if (!_mainWindow.IsOpen)
            {
                _mainWindow.IsOpen = true;
                _mainWindow.OpenReason = MainWindow.EOpenReason.NearFabricationStation;
            }

            if (_mainWindow.State is MainWindow.ButtonState.Pause or MainWindow.ButtonState.Stop)
            {
                _mainWindow.State = MainWindow.ButtonState.None;
                if (CurrentStage != Stage.Stopped)
                {
                    _externalPluginHandler.Restore();
                    CurrentStage = Stage.Stopped;
                }

                return;
            }
            else if (_mainWindow.State is MainWindow.ButtonState.Start or MainWindow.ButtonState.Resume &&
                     CurrentStage == Stage.Stopped)
            {
                // TODO Error checking, we should ensure the player has the required job level for *all* crafting parts
                _mainWindow.State = MainWindow.ButtonState.None;
                CurrentStage = Stage.TakeItemFromQueue;
            }

            if (CurrentStage != Stage.Stopped && CurrentStage != Stage.RequestStop && !_externalPluginHandler.Saved)
                _externalPluginHandler.Save();

            switch (CurrentStage)
            {
                case Stage.TakeItemFromQueue:
                    if (CheckContinueWithDelivery())
                        CurrentStage = Stage.ContributeMaterials;
                    else
                        TakeItemFromQueue();
                    break;

                case Stage.TargetFabricationStation:
                    if (_configuration.CurrentlyCraftedItem is { StartedCrafting: true })
                        CurrentStage = Stage.SelectCraftBranch;
                    else
                        CurrentStage = Stage.OpenCraftingLog;

                    InteractWithFabricationStation(fabricationStation!);

                    break;

                case Stage.OpenCraftingLog:
                    OpenCraftingLog();
                    break;

                case Stage.SelectCraftCategory:
                    SelectCraftCategory();
                    break;

                case Stage.SelectCraft:
                    SelectCraft();
                    break;

                case Stage.ConfirmCraft:
                    ConfirmCraft();
                    break;

                case Stage.RequestStop:
                    _externalPluginHandler.Restore();
                    CurrentStage = Stage.Stopped;
                    break;

                case Stage.SelectCraftBranch:
                    SelectCraftBranch();
                    break;

                case Stage.ContributeMaterials:
                    ContributeMaterials();
                    break;

                case Stage.OpenRequestItemWindow:
                    // see RequestPostSetup and related
                    if (DateTime.Now > _fallbackAt)
                        goto case Stage.ContributeMaterials;
                    break;

                case Stage.OpenRequestItemSelect:
                case Stage.ConfirmRequestItemWindow:
                    // see RequestPostSetup and related
                    break;


                case Stage.ConfirmMaterialDelivery:
                    // see SelectYesNoPostSetup
                    break;

                case Stage.ConfirmCollectProduct:
                    // see SelectYesNoPostSetup
                    break;

                case Stage.Stopped:
                    break;

                default:
                    _pluginLog.Warning($"Unknown stage {CurrentStage}");
                    break;
            }
        }
    }

    private WorkshopCraft GetCurrentCraft()
    {
        return _workshopCache.Crafts.Single(
            x => x.WorkshopItemId == _configuration.CurrentlyCraftedItem!.WorkshopItemId);
    }

    private void ProcessCommand(string command, string arguments)
    {
        if (arguments is "c" or "config")
            _configWindow.Toggle();
        else
            _mainWindow.Toggle(MainWindow.EOpenReason.Command);
    }

    private void ProcessBuyCommand(string command, string arguments)
    {
        if (_ceruleumTankWindow.TryParseBuyRequest(arguments, out int missingQuantity))
            _ceruleumTankWindow.StartPurchase(missingQuantity);
        else
            _chatGui.PrintError($"Usage: {command} <stacks>");
    }

    private void ProcessFillCommand(string command, string arguments)
    {
        if (_ceruleumTankWindow.TryParseFillRequest(arguments, out int missingQuantity))
            _ceruleumTankWindow.StartPurchase(missingQuantity);
        else
            _chatGui.PrintError($"Usage: {command} <stacks>");
    }

    private void OpenMainUi()
        => _mainWindow.Toggle(MainWindow.EOpenReason.PluginInstaller);

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "ContextIconMenu", ContextIconMenuPostReceiveEvent);
        _addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "Request", RequestPostRefresh);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Request", RequestPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", SelectYesNoPostSetup);
        _commandManager.RemoveHandler("/fill-tanks");
        _commandManager.RemoveHandler("/buy-tanks");
        _commandManager.RemoveHandler("/workshoppa");
        _commandManager.RemoveHandler("/ws");
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        _framework.Update -= FrameworkUpdate;

        _ceruleumTankWindow.Dispose();
        _repairKitWindow.Dispose();
        _retainerDepositWindow.StopTransfer();

        _externalPluginHandler.RestoreTextAdvance();
        _externalPluginHandler.Restore();
    }
}
