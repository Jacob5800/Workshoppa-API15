Warning: truncated output (original token count: 7599)
Total output lines: 745

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;
using LLib.ImGui;
using Lumina.Excel.Sheets;

namespace Workshoppa.Windows;

internal sealed unsafe class RetainerDepositWindow : LWindow
{
    // AgentInventoryContext callback parameter documented for AgentRetainer's entrust action.
    private const ulong EntrustToRetainerCallback = 1;
    private static readonly TimeSpan MoveConfirmationStability = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MoveRollbackGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailedMoveRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(10);
    private static readonly InventoryType[] PlayerInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGameGui _gameGui;
    private readonly Configuration _configuration;
    private readonly IPluginLog _pluginLog;
    private readonly Dictionary<uint, ItemDetails> _items;
    private List<InventoryItemOption> _inventoryItems = new();

    private string _exclusionSearch = string.Empty;
    private string _inventoryScanStatus = "Inventory has not been scanned yet.";
    private string _status = string.Empty;
    private uint _selectedKeepItemId;
    private bool _hasInventoryScan;
    private bool _wasRetainerWindowOpen;
    private bool _autoOpened;
    private bool _transferActive;
    private ulong _transferRetainerId;
    private PendingMove? _pendingMove;
    private DateTime _pendingSince;
    private DateTime _observedMoveAt;
    private DateTime _observedMoveMissingAt;
    private int _observedMoveQuantity;
    private DateTime _nextMoveAt;
    private readonly HashSet<MoveSource> _failedSources = new();
    private int _completedMoves;
    private int _movedItems;

    public RetainerDepositWindow(IDalamudPluginInterface pluginInterface, IGameGui gameGui, IDataManager dataManager,
        Configuration configuration, IPluginLog pluginLog)
        : base("Retainer Deposit###WorkshoppaRetainerDepositWindow")
    {
        _pluginInterface = pluginInterface;
        _gameGui = gameGui;
        _configuration = configuration;
        _pluginLog = pluginLog;
        _configuration.RetainerDepositExcludedItemIds ??= new List<uint>();

        _items = dataManager.GetExcelSheet<Item>()
            .Where(x => x.RowId > 0)
            .Select(x => new ItemDetails(x.RowId, x.Name.ToString(), x.StackSize))
            .ToDictionary(x => x.Id);

        Position = new Vector2(180, 140);
        PositionCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(520, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 400),
            MaximumSize = new Vector2(720, 850),
        };
    }

    public void ToggleFromMenu()
    {
        if (IsOpen)
        {
            IsOpen = false;
            StopTransfer();
        }
        else
        {
            IsOpenAndUncollapsed = true;
            _autoOpened = false;
        }
    }

    public void UpdateRetainerWindowState()
    {
        bool isOpen = IsRetainerTransferWindowOpen();
        if (isOpen && !_wasRetainerWindowOpen)
        {
            IsOpenAndUncollapsed = true;
            _autoOpened = true;
        }
        else if (!isOpen && _wasRetainerWindowOpen)
        {
            StopTransfer();
            if (_autoOpened)
                IsOpen = false;
            _autoOpened = false;
        }

        _wasRetainerWindowOpen = isOpen;
    }

    public void UpdateTransfer()
    {
        if (!_transferActive)
            return;

        if (!IsRetainerTransferWindowOpen() || !TryGetActiveRetainerId(out ulong retainerId) ||
            retainerId != _transferRetainerId)
        {
            StopTransfer("Stopped because the retainer transfer window or active retainer changed.");
            return;
        }

        InventoryManager* inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            StopTransfer("Stopped because the inventory manager is unavailable.");
            return;
        }

        if (_pendingMove is { } pending)
        {
            DateTime now = DateTime.UtcNow;
            if (TryGetAppliedMove(inventoryManager, pending, out int movedQuantity))
            {
                _observedMoveMissingAt = DateTime.MinValue;
                if (_observedMoveQuantity != movedQuantity)
                {
                    _observedMoveQuantity = movedQuantity;
                    _observedMoveAt = now;
                }

                if (now - _observedMoveAt >= MoveConfirmationStability)
                {
                    _completedMoves++;
                    _movedItems += movedQuantity;
                    string itemName = _items.TryGetValue(pending.ItemId, out var itemDetails)
                        ? itemDetails.Name
                        : $"item {pending.ItemId}";
                    _pluginLog.Information(
                        $"Retainer deposit confirmed for {movedQuantity:N0} x {itemName} from {pending.SourceType}[{pending.SourceSlot}].");
                    _pendingMove = null;
                    _observedMoveQuantity = 0;
                    _observedMoveMissingAt = DateTime.MinValue;
                    _nextMoveAt = now;
                    _status = $"Deposited {_movedItems:N0} items across {_completedMoves:N0} moves…";
                }
            }
            else
            {
                if (_observedMoveQuantity > 0)
                {
                    if (_observedMoveMissingAt == DateTime.MinValue)
                        _observedMoveMissingAt = now;

                    if (now - _observedMoveMissingAt >= MoveRollbackGrace)
                    {
                        string itemName = _items.TryGetValue(pending.ItemId, out var itemDetails)
                            ? itemDetails.Name
                            : $"item {pending.ItemId}";
                        _pluginLog.Warning(
                            $"Retainer transfer for {itemName} reverted before its inventory change was confirmed.");
                        StopTransfer($"Stopped because {itemName} reverted before the transfer could be confirmed.");
                        return;
                    }
                }
            }

            if (_pendingMove is not null && now - _pendingSince > MoveTimeout)
            {
                _failedSources.Add(new MoveSource(pending.SourceType, pending.SourceSlot, pending.ItemId,
                    pending.Flags));
                string itemName = _items.TryGetValue(pending.ItemId, out var itemDetails)
                    ? itemDetails.Name
                    : $"item {pending.ItemId}";
                _pluginLog.Warning(
                    $"Retainer deposit was not confirmed for {itemName} from {pending.SourceType}[{pending.SourceSlot}].");
                _pendingMove = null;
                _observedMoveQuantity = 0;
                _observedMoveMissingAt = DateTime.MinValue;
                _nextMoveAt = now + FailedMoveRetryDelay;
            }
            else if (_pendingMove is not null)
            {
                return;
            }
        }

        if (DateTime.UtcNow < _nextMoveAt)
            return;

        if (!IsRetainerTransferWindowReady())
        {
            _status = "Waiting for the retainer inve…3599 tokens truncated…yItem* inventoryItem = container->GetInventorySlot(slotIndex);
                if (inventoryItem == null || inventoryItem->ItemId == 0 || inventoryItem->Quantity <= 0)
                    continue;

                if (_configuration.RetainerDepositExcludedItemIds.Contains(inventoryItem->ItemId))
                {
                    excludedStackCount++;
                    excludedItemCount += inventoryItem->Quantity;
                }
                else
                {
                    stackCount++;
                    itemCount += inventoryItem->Quantity;
                }
            }
        }

        return (stackCount, itemCount, excludedStackCount, excludedItemCount);
    }

    private static int CountFreeRetainerSlots(InventoryManager* inventoryManager)
    {
        int freeSlots = 0;
        for (int page = 0; page < 7; ++page)
        {
            InventoryType inventoryType = (InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page);
            InventoryContainer* container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (int slotIndex = 0; slotIndex < container->Size; ++slotIndex)
            {
                InventoryItem* inventoryItem = container->GetInventorySlot(slotIndex);
                if (inventoryItem != null && inventoryItem->ItemId == 0)
                    freeSlots++;
            }
        }

        return freeSlots;
    }

    private bool TryFindNextMove(InventoryManager* inventoryManager, out PendingMove pendingMove)
    {
        foreach (InventoryType sourceType in PlayerInventories)
        {
            InventoryContainer* sourceContainer = inventoryManager->GetInventoryContainer(sourceType);
            if (sourceContainer == null || !sourceContainer->IsLoaded)
                continue;

            for (int sourceIndex = 0; sourceIndex < sourceContainer->Size; ++sourceIndex)
            {
                InventoryItem* source = sourceContainer->GetInventorySlot(sourceIndex);
                if (source == null || source->ItemId == 0 || source->Quantity <= 0 ||
                    _configuration.RetainerDepositExcludedItemIds.Contains(source->ItemId) ||
                    _failedSources.Contains(new MoveSource(sourceType, checked((ushort)sourceIndex),
                        source->ItemId, source->Flags)))
                    continue;

                if (!TryFindDestination(inventoryManager, sourceType, source, sourceIndex,
                        preferExistingStack: true, out pendingMove) &&
                    !TryFindDestination(inventoryManager, sourceType, source, sourceIndex,
                        preferExistingStack: false, out pendingMove))
                    continue;

                return true;
            }
        }

        pendingMove = default;
        return false;
    }

    private bool TryFindDestination(InventoryManager* inventoryManager, InventoryType sourceType,
        InventoryItem* source, int sourceIndex, bool preferExistingStack, out PendingMove pendingMove)
    {
        uint maxStackSize = _items.TryGetValue(source->ItemId, out var itemDetails) ? itemDetails.StackSize : 1;
        if (!preferExistingStack || maxStackSize <= 1 || source->SpiritbondOrCollectability != 0)
        {
            if (preferExistingStack)
            {
                pendingMove = default;
                return false;
            }
        }

        for (int page = 0; page < 7; ++page)
        {
            InventoryType destinationType = (InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page);
            InventoryContainer* destinationContainer = inventoryManager->GetInventoryContainer(destinationType);
            if (destinationContainer == null || !destinationContainer->IsLoaded)
                continue;

            for (int destinationIndex = 0; destinationIndex < destinationContainer->Size; ++destinationIndex)
            {
                if (destinationType == sourceType && destinationIndex == sourceIndex)
                    continue;

                InventoryItem* destination = destinationContainer->GetInventorySlot(destinationIndex);
                if (destination == null)
                    continue;

                if (preferExistingStack)
                {
                    if (destination->ItemId != source->ItemId || destination->Flags != source->Flags ||
                        destination->SpiritbondOrCollectability != source->SpiritbondOrCollectability ||
                        destination->Quantity >= maxStackSize ||
                        maxStackSize - destination->Quantity < source->Quantity)
                        continue;
                }
                else if (destination->ItemId != 0)
                {
                    continue;
                }

                pendingMove = new PendingMove(sourceType, checked((ushort)sourceIndex), source->ItemId,
                    source->Flags, source->Quantity,
                    CountRetainerItems(inventoryManager, source->ItemId, source->Flags));
                return true;
            }
        }

        pendingMove = default;
        return false;
    }

    private static bool TryGetAppliedMove(InventoryManager* inventoryManager, PendingMove pendingMove,
        out int movedQuantity)
    {
        InventoryContainer* sourceContainer = inventoryManager->GetInventoryContainer(pendingMove.SourceType);
        if (sourceContainer == null || !sourceContainer->IsLoaded)
        {
            movedQuantity = 0;
            return false;
        }

        InventoryItem* source = sourceContainer->GetInventorySlot(pendingMove.SourceSlot);
        if (source == null)
        {
            movedQuantity = 0;
            return false;
        }

        int remaining = source->ItemId == pendingMove.ItemId && source->Flags == pendingMove.Flags
            ? source->Quantity
            : 0;
        int sourceDecrease = pendingMove.SourceQuantity - remaining;
        int retainerIncrease = CountRetainerItems(inventoryManager, pendingMove.ItemId, pendingMove.Flags) -
                               pendingMove.RetainerItemCount;
        if (sourceDecrease <= 0 || retainerIncrease <= 0 || sourceDecrease != retainerIncrease)
        {
            movedQuantity = 0;
            return false;
        }

        movedQuantity = sourceDecrease;
        return movedQuantity > 0;
    }

    private static int CountRetainerItems(InventoryManager* inventoryManager, uint itemId,
        InventoryItem.ItemFlags flags)
    {
        int itemCount = 0;
        for (int page = 0; page < 7; ++page)
        {
            InventoryType inventoryType = (InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page);
            InventoryContainer* container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (int slotIndex = 0; slotIndex < container->Size; ++slotIndex)
            {
                InventoryItem* item = container->GetInventorySlot(slotIndex);
                if (item != null && item->ItemId == itemId && item->Flags == flags)
                    itemCount += item->Quantity;
            }
        }

        return itemCount;
    }

    private readonly record struct ItemDetails(uint Id, string Name, uint StackSize);
    private readonly record struct InventoryItemOption(uint Id, string Name, int Quantity);
    private readonly record struct MoveSource(InventoryType SourceType, ushort SourceSlot, uint ItemId,
        InventoryItem.ItemFlags Flags);
    private readonly record struct PendingMove(InventoryType SourceType, ushort SourceSlot, uint ItemId,
        InventoryItem.ItemFlags Flags, int SourceQuantity, int RetainerItemCount);
    private readonly record struct Snapshot(int StackCount, int ItemCount, int ExcludedStackCount,
        int ExcludedItemCount, int FreeSlots);
}
