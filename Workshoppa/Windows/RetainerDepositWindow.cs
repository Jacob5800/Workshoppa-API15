using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;
using LLib.ImGui;
using Lumina.Excel.Sheets;

namespace Workshoppa.Windows;

internal sealed unsafe class RetainerDepositWindow : LWindow
{
    private static readonly TimeSpan MoveCooldown = TimeSpan.FromMilliseconds(500);
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
    private DateTime _nextMoveAt;
    private readonly HashSet<MovePair> _failedMoves = new();
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
            if (WasMoveApplied(inventoryManager, pending, out int movedQuantity))
            {
                _completedMoves++;
                _movedItems += movedQuantity;
                _pendingMove = null;
                _nextMoveAt = DateTime.UtcNow + MoveCooldown;
                _status = $"Deposited {_movedItems:N0} items across {_completedMoves:N0} moves…";
            }
            else if (DateTime.UtcNow - _pendingSince > TimeSpan.FromSeconds(4))
            {
                _failedMoves.Add(new MovePair(pending.SourceType, pending.SourceSlot,
                    pending.DestinationType, pending.DestinationSlot));
                if (pending.DestinationItemId == 0)
                    _failedSources.Add(new MoveSource(pending.SourceType, pending.SourceSlot));
                _pluginLog.Warning(
                    $"Retainer deposit did not complete for item {pending.ItemId} from {pending.SourceType}[{pending.SourceSlot}] to {pending.DestinationType}[{pending.DestinationSlot}].");
                _pendingMove = null;
                _nextMoveAt = DateTime.UtcNow + MoveCooldown;
            }
            else
            {
                return;
            }
        }

        if (DateTime.UtcNow < _nextMoveAt)
            return;

        if (TryFindNextMove(inventoryManager, out PendingMove nextMove))
        {
            _pendingMove = nextMove;
            _pendingSince = DateTime.UtcNow;
            inventoryManager->MoveItemSlot(nextMove.SourceType, nextMove.SourceSlot,
                nextMove.DestinationType, nextMove.DestinationSlot);
            string itemName = _items.TryGetValue(nextMove.ItemId, out var itemDetails)
                ? itemDetails.Name
                : $"item {nextMove.ItemId}";
            _status = $"Depositing {itemName}…";
            return;
        }

        if (CountEligibleItems(inventoryManager).StackCount == 0)
        {
            StopTransfer($"Finished: moved {_movedItems:N0} items across {_completedMoves:N0} moves.");
        }
        else
        {
            StopTransfer($"Stopped: some eligible items could not be accepted or retainer storage is full. Moved {_movedItems:N0} items across {_completedMoves:N0} moves.");
        }
    }

    public void StopTransfer()
        => StopTransfer("Deposit stopped.");

    public override void OnClose()
    {
        StopTransfer();
        base.OnClose();
    }

    public override void DrawContent()
    {
        if (!_hasInventoryScan)
            ScanInventory();

        ImGui.TextWrapped("Move inventory stacks into the active retainer one at a time, with a short pause between moves. Nothing moves until you press Start deposit.");
        ImGui.Separator();

        bool retainerWindowOpen = IsRetainerTransferWindowOpen();
        bool retainerSelected = TryGetActiveRetainerId(out _);
        Snapshot snapshot = GetSnapshot();
        ImGui.Text("1. Open the retainer screen");
        if (!retainerWindowOpen)
            ImGui.TextWrapped("At a Summoning Bell, speak to your retainer and choose “Entrust or withdraw items.” Workshoppa opens this window automatically. If needed, type /ws, then choose Retainer Deposit from Workshoppa’s top menu. Keep the retainer screen open while depositing.");
        else if (!retainerSelected)
            ImGui.TextWrapped("The retainer window is open. Waiting for the active retainer to finish loading.");
        else
            ImGui.Text("Retainer ready.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("2. Set restrictions — items to keep");
        ImGui.TextWrapped("Items you add here are skipped and stay in your inventory. The rule applies to every stack across your four inventory bags.");
        DrawExclusions();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("3. Review and start");
        ImGui.BulletText($"Will deposit: {snapshot.StackCount:N0} stacks / {snapshot.ItemCount:N0} items");
        ImGui.BulletText($"Will be kept: {snapshot.ExcludedStackCount:N0} stacks / {snapshot.ExcludedItemCount:N0} items");
        if (retainerWindowOpen && retainerSelected)
            ImGui.BulletText($"Retainer space: {snapshot.FreeSlots:N0} empty slots, plus compatible stacks");

        if (_transferActive)
        {
            ImGui.TextWrapped(_status);
            if (ImGui.Button("STOP DEPOSIT", new Vector2(-1, 0)))
                StopTransfer();
        }
        else
        {
            ImGui.BeginDisabled(!retainerWindowOpen || !retainerSelected || snapshot.StackCount == 0);
            if (ImGui.Button("Start deposit", new Vector2(-1, 0)))
                StartTransfer();
            ImGui.EndDisabled();
            if (!retainerWindowOpen)
                ImGui.TextDisabled("Open the retainer screen in step 1 to enable depositing.");
            else if (!retainerSelected)
                ImGui.TextDisabled("Waiting for the active retainer to load.");
            else if (snapshot.StackCount == 0)
                ImGui.TextDisabled("Nothing eligible to deposit. Check your keep list in step 2.");

            if (!string.IsNullOrEmpty(_status))
                ImGui.TextWrapped(_status);
        }
    }

    private void DrawExclusions()
    {
        if (_configuration.RetainerDepositExcludedItemIds == null)
            _configuration.RetainerDepositExcludedItemIds = new List<uint>();

        ImGui.Text("Your inventory items");
        if (ImGui.Button("Scan inventory now"))
            ScanInventory();
        ImGui.SameLine();
        ImGui.TextDisabled(_inventoryScanStatus);
        ImGui.TextDisabled("Scanning only lists items; it does not move them.");
        ImGui.TextDisabled("Filter if needed, choose an item, then add it to the keep list.");

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##RetainerExclusionSearch", "Filter inventory items (optional)…", ref _exclusionSearch, 128);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        InventoryItemOption selectedItem = _inventoryItems.FirstOrDefault(x => x.Id == _selectedKeepItemId);
        string preview = selectedItem.Id == _selectedKeepItemId && _selectedKeepItemId != 0
            ? $"{selectedItem.Name} ({selectedItem.Quantity} in inventory)"
            : "Choose an item to keep";
        ImGui.BeginDisabled(!_hasInventoryScan || _inventoryItems.Count == 0);
        if (ImGui.BeginCombo("Item to keep", preview, ImGuiComboFlags.HeightLarge))
        {
            int availableItems = 0;
            foreach (InventoryItemOption item in _inventoryItems)
            {
                if (_configuration.RetainerDepositExcludedItemIds.Contains(item.Id) ||
                    !item.Name.Contains(_exclusionSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                availableItems++;
                if (ImGui.Selectable($"{item.Name}  ({item.Quantity} in inventory)##KeepItem{item.Id}",
                        _selectedKeepItemId == item.Id))
                {
                    _selectedKeepItemId = item.Id;
                    ImGui.CloseCurrentPopup();
                }
            }

            if (availableItems == 0)
            {
                ImGui.TextDisabled("No available inventory items match this filter.");
            }

            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        bool selectedCanBeAdded = selectedItem.Id == _selectedKeepItemId && _selectedKeepItemId != 0 &&
                                  !_configuration.RetainerDepositExcludedItemIds.Contains(_selectedKeepItemId);
        ImGui.BeginDisabled(!selectedCanBeAdded);
        if (ImGui.Button("Add selected item to keep list", new Vector2(-1, 0)))
        {
            _configuration.RetainerDepositExcludedItemIds.Add(_selectedKeepItemId);
            _selectedKeepItemId = 0;
            SaveConfiguration();
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Text("Items being kept");
        if (_configuration.RetainerDepositExcludedItemIds.Count == 0)
        {
            ImGui.TextDisabled("Empty — all inventory items are eligible to deposit.");
            return;
        }

        uint? removeItemId = null;
        foreach (var excludedItem in _configuration.RetainerDepositExcludedItemIds
                     .Distinct()
                     .Select(id => new
                     {
                         Id = id,
                         Name = _items.TryGetValue(id, out var item) ? item.Name : $"Item {id}",
                     })
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            uint itemId = excludedItem.Id;
            InventoryItemOption inventoryItem = _inventoryItems.FirstOrDefault(x => x.Id == itemId);
            string amount = inventoryItem.Id == itemId
                ? $"{inventoryItem.Quantity} in inventory"
                : "not in scanned inventory";
            ImGui.BulletText($"{excludedItem.Name}  ({amount})");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##Excluded{itemId}"))
                removeItemId = itemId;
        }

        if (removeItemId is { } id)
        {
            _configuration.RetainerDepositExcludedItemIds.RemoveAll(x => x == id);
            SaveConfiguration();
        }
    }

    private void ScanInventory()
    {
        InventoryManager* inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            _hasInventoryScan = false;
            _inventoryItems.Clear();
            _inventoryScanStatus = "Inventory is not ready yet.";
            return;
        }

        var quantities = new Dictionary<uint, int>();
        int loadedContainers = 0;
        foreach (InventoryType inventoryType in PlayerInventories)
        {
            InventoryContainer* container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            loadedContainers++;
            for (int slotIndex = 0; slotIndex < container->Size; ++slotIndex)
            {
                InventoryItem* inventoryItem = container->GetInventorySlot(slotIndex);
                if (inventoryItem == null || inventoryItem->ItemId == 0 || inventoryItem->Quantity <= 0)
                    continue;

                quantities[inventoryItem->ItemId] = quantities.GetValueOrDefault(inventoryItem->ItemId) +
                                                    inventoryItem->Quantity;
            }
        }

        if (loadedContainers == 0)
        {
            _hasInventoryScan = false;
            _inventoryItems.Clear();
            _inventoryScanStatus = "Inventory bags are still loading; scan again shortly.";
            return;
        }

        _inventoryItems = quantities
            .Select(x => new InventoryItemOption(x.Key,
                _items.TryGetValue(x.Key, out ItemDetails details) ? details.Name : $"Item {x.Key}", x.Value))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _hasInventoryScan = true;
        _inventoryScanStatus = $"{_inventoryItems.Count:N0} item types found in {loadedContainers}/4 bags.";
    }

    private void StartTransfer()
    {
        if (!IsRetainerTransferWindowOpen() || !TryGetActiveRetainerId(out ulong retainerId))
            return;

        _transferRetainerId = retainerId;
        _transferActive = true;
        _pendingMove = null;
        _nextMoveAt = DateTime.MinValue;
        _failedMoves.Clear();
        _failedSources.Clear();
        _completedMoves = 0;
        _movedItems = 0;
        _status = "Starting deposit…";
    }

    private void StopTransfer(string status)
    {
        _transferActive = false;
        _pendingMove = null;
        _failedMoves.Clear();
        _failedSources.Clear();
        _status = status;
    }

    private void SaveConfiguration()
        => _pluginInterface.SavePluginConfig(_configuration);

    private bool IsRetainerTransferWindowOpen()
    {
        // The actual retainer inventory is shown by InventoryRetainer (or its large-layout
        // variant). RetainerItemTransferList is only the Entrust Duplicates confirmation popup.
        return IsAddonVisible("InventoryRetainerLarge") || IsAddonVisible("InventoryRetainer");
    }

    private bool IsAddonVisible(string addonName)
        => _gameGui.TryGetAddonByName<AtkUnitBase>(addonName, out var addon) && addon->IsVisible;

    private static bool TryGetActiveRetainerId(out ulong retainerId)
    {
        retainerId = 0;
        RetainerManager* retainerManager = RetainerManager.Instance();
        if (retainerManager == null || !retainerManager->IsReady)
            return false;

        RetainerManager.Retainer* activeRetainer = retainerManager->GetActiveRetainer();
        if (activeRetainer == null || activeRetainer->RetainerId == 0)
            return false;

        retainerId = activeRetainer->RetainerId;
        return true;
    }

    private Snapshot GetSnapshot()
    {
        InventoryManager* inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return default;

        var (stackCount, itemCount, excludedStackCount, excludedItemCount) = CountEligibleItems(inventoryManager);
        return new Snapshot(stackCount, itemCount, excludedStackCount, excludedItemCount,
            CountFreeRetainerSlots(inventoryManager));
    }

    private (int StackCount, int ItemCount, int ExcludedStackCount, int ExcludedItemCount) CountEligibleItems(
        InventoryManager* inventoryManager)
    {
        int stackCount = 0;
        int itemCount = 0;
        int excludedStackCount = 0;
        int excludedItemCount = 0;
        foreach (InventoryType inventoryType in PlayerInventories)
        {
            InventoryContainer* container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (int slotIndex = 0; slotIndex < container->Size; ++slotIndex)
            {
                InventoryItem* inventoryItem = container->GetInventorySlot(slotIndex);
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
                    _failedSources.Contains(new MoveSource(sourceType, checked((ushort)sourceIndex))))
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

                var pair = new MovePair(sourceType, checked((ushort)sourceIndex), destinationType,
                    checked((ushort)destinationIndex));
                if (_failedMoves.Contains(pair))
                    continue;

                InventoryItem* destination = destinationContainer->GetInventorySlot(destinationIndex);
                if (destination == null)
                    continue;

                if (preferExistingStack)
                {
                    if (destination->ItemId != source->ItemId || destination->Flags != source->Flags ||
                        destination->SpiritbondOrCollectability != source->SpiritbondOrCollectability ||
                        destination->Quantity >= maxStackSize)
                        continue;
                }
                else if (destination->ItemId != 0)
                {
                    continue;
                }

                pendingMove = new PendingMove(sourceType, checked((ushort)sourceIndex), source->ItemId,
                    source->Quantity, destinationType, checked((ushort)destinationIndex), destination->ItemId,
                    destination->Quantity);
                return true;
            }
        }

        pendingMove = default;
        return false;
    }

    private static bool WasMoveApplied(InventoryManager* inventoryManager, PendingMove pendingMove,
        out int movedQuantity)
    {
        InventoryContainer* sourceContainer = inventoryManager->GetInventoryContainer(pendingMove.SourceType);
        InventoryContainer* destinationContainer = inventoryManager->GetInventoryContainer(pendingMove.DestinationType);
        if (sourceContainer == null || destinationContainer == null || !sourceContainer->IsLoaded ||
            !destinationContainer->IsLoaded)
        {
            movedQuantity = 0;
            return false;
        }

        InventoryItem* source = sourceContainer->GetInventorySlot(pendingMove.SourceSlot);
        InventoryItem* destination = destinationContainer->GetInventorySlot(pendingMove.DestinationSlot);
        if (source == null || destination == null)
        {
            movedQuantity = 0;
            return false;
        }

        int remaining = source->ItemId == pendingMove.ItemId ? source->Quantity : 0;
        bool destinationChanged = destination->ItemId == pendingMove.ItemId &&
                                 destination->Quantity > pendingMove.DestinationQuantity;
        if (remaining >= pendingMove.SourceQuantity || !destinationChanged)
        {
            movedQuantity = 0;
            return false;
        }

        movedQuantity = pendingMove.SourceQuantity - remaining;
        return movedQuantity > 0;
    }

    private readonly record struct ItemDetails(uint Id, string Name, uint StackSize);
    private readonly record struct InventoryItemOption(uint Id, string Name, int Quantity);
    private readonly record struct MoveSource(InventoryType SourceType, ushort SourceSlot);
    private readonly record struct MovePair(InventoryType SourceType, ushort SourceSlot,
        InventoryType DestinationType, ushort DestinationSlot);
    private readonly record struct PendingMove(InventoryType SourceType, ushort SourceSlot, uint ItemId,
        int SourceQuantity, InventoryType DestinationType, ushort DestinationSlot, uint DestinationItemId,
        int DestinationQuantity);
    private readonly record struct Snapshot(int StackCount, int ItemCount, int ExcludedStackCount,
        int ExcludedItemCount, int FreeSlots);
}
